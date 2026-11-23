using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// The 20 answers of one source: scored in code from the model's answers and their quotes, and the question put to
// the model with the answer it must give.
public sealed partial class MultivocalQualityAssessor
{
    /// <summary>
    /// The 20 answers: the three decided in code, and the model's 17 with their quotes checked. A point the model gives
    /// without a quote found in the text is not given (its reason says so); for 3.3, asked the other way round, a
    /// lowered score needs the quote and an unsupported one is raised back to 1.
    /// </summary>
    public static List<QualityAnswer> Score(GreyRecord record, IReadOnlyList<ModelQualityAnswer> model, string text)
    {
        var byId = model.GroupBy(a => a.Id.Trim()).ToDictionary(g => g.Key, g => g.First());
        var answers = new List<QualityAnswer>();
        foreach (var item in GreyQualityChecklist.Items)
        {
            if (item.ByCode)
            {
                var (score, why) = item.Id switch
                {
                    "4.1" => GreyQualityChecklist.Date(record.Published),
                    "7.1" => GreyQualityChecklist.Impact(record.Signals),
                    _ => GreyQualityChecklist.OutletTier(record.Kind),
                };
                answers.Add(new QualityAnswer { Id = item.Id, Score = score, Reason = why, Basis = "code" });
                continue;
            }

            byId.TryGetValue(item.Id, out var a);
            double given = a == null ? 0 : Snap(a.Score);
            string reason = a?.Reason.Trim() ?? "The model gave no answer for this item.";
            string? quote = string.IsNullOrWhiteSpace(a?.Quote) ? null : a!.Quote!.Trim();
            bool found = quote != null && CitationSupportChecker.QuoteOccursIn(quote, text);
            bool needsQuote = item.QuoteForLow ? given < 1 : given > 0;
            var answer = new QualityAnswer { Id = item.Id, Score = given, Reason = reason, Quote = quote, Basis = "model" };
            if (needsQuote && !found)
            {
                answer.Score = item.QuoteForLow ? 1 : 0;
                answer.Basis = "lowered";
                answer.Reason = (item.QuoteForLow
                    ? "Scored 1: the model's evidence of a vested interest was not found in the page. "
                    : "Scored 0: the model's quote was not found in the page, so its points were not given. ") + reason;
            }
            answers.Add(answer);
        }
        return answers;
    }

    /// <summary>As in the paper's example, a source at or above the threshold passes.</summary>
    public static string OutcomeFor(double points, int threshold) => points >= threshold ? "Passed" : "Below threshold";

    private static double Snap(double score) => score >= 0.75 ? 1 : score >= 0.25 ? 0.5 : 0;

    /// <summary>
    /// The argument record of an opinion piece, with every quote checked against the page: a claim or answer whose
    /// quote is not in the page is kept but marked as unsupported. Null when the source is not an opinion piece.
    /// </summary>
    public static GreyArgument? Argument(ModelOpinion? opinion, string text)
    {
        if (opinion is not { IsOpinion: true }) return null;
        var argument = new GreyArgument
        {
            Claim = opinion.Claim?.Trim() ?? "",
            ClaimFound = CitationSupportChecker.QuoteOccursIn(opinion.Claim, text),
        };
        foreach (var (id, question) in GreyArgument.CriticalQuestions)
        {
            var answer = new ArgumentAnswer { Id = id, Question = question };
            if (id == "trustworthiness")
            {
                answer.Answer = "Left to the reviewer: Garousi et al. note that this cannot be judged reliably.";
                answer.Basis = "reviewer";
            }
            else if (id == "consistency")
            {
                answer.Answer = "Judged in the synthesis, where the claim is compared with the other sources; experts may also disagree for good reasons.";
                answer.Basis = "synthesis";
            }
            else
            {
                var given = id switch { "expertise" => opinion.Expertise, "field" => opinion.Field, "opinion" => opinion.Assertion, _ => opinion.Evidence };
                answer.Answer = given?.Answer?.Trim() ?? "No answer.";
                answer.Quote = string.IsNullOrWhiteSpace(given?.Quote) ? null : given!.Quote!.Trim();
                bool found = answer.Quote != null && CitationSupportChecker.QuoteOccursIn(answer.Quote, text);
                answer.Basis = found ? "model" : "unsupported";
                if (!found) answer.Answer = "(No quote from the page backs this.) " + answer.Answer;
            }
            argument.Questions.Add(answer);
        }
        return argument;
    }

    private async Task<(ModelQualityAnswers Answers, bool FromCache)> AnswerCachedAsync(
        IChatCompletionService chat, MultivocalPlan plan, GreyRecord record, string text, string textSha)
    {
        string topic = TopicLine(plan);
        string key = ReviewCache.Key(PromptVersion, _model, topic, record.Title, record.Producer, record.Site, record.Kind, textSha);
        if (_cache.TryGet<ModelQualityAnswers>("grey-quality", key, out var cached) && cached != null && ModelQualityAnswers.Validate(cached) == null)
            return (cached, true);
        var answer = await AskAsync(chat, topic, record, text);
        _cache.Set("grey-quality", key, answer);
        return (answer, false);
    }

    private static string TopicLine(MultivocalPlan plan) =>
        $"{plan.Topic}. Research questions: {string.Join(" ", plan.Numbered().Select(q => $"{q.Number}: {q.Question.Text}"))}";

    private const string AnswerShape = """
        {"answers":[{"id":"1.1","score":1,"reason":"One sentence.","quote":"Word for word from the page."}],"opinion":{"isOpinion":true,"claim":"Word for word from the page.","expertise":{"answer":"One sentence.","quote":"Word for word."},"field":{"answer":"...","quote":"..."},"assertion":{"answer":"...","quote":"..."},"evidence":{"answer":"...","quote":"..."}}}
        """;

    /// <summary>
    /// The quality prompt: the 17 items read from the page, each scored 1, 0.5 or 0 with a verbatim quote, and for an
    /// opinion piece its main claim and four of the critical questions for an expert opinion (G13).
    /// </summary>
    public static async Task<ModelQualityAnswers> AskAsync(IChatCompletionService chat, string topic, GreyRecord record, string text)
    {
        string items = string.Join("\n", GreyQualityChecklist.ModelItems.Select(i => $"{i.Id} ({i.Group}) {i.Question}"));
        string about = $"""
            - Title: {record.Title}
            - Address: {record.Url}
            - Published on: {record.Site}
            - By: {(record.Producer.Length > 0 ? record.Producer : "not given")}
            """;
        var prompt = $$"""
            You assess the quality of one grey literature source for a multivocal literature review, with the quality checklist of Garousi et al. (2019, Table 7).
            The review is about: {{PromptSafety.Wrap(topic, "review topic")}}

            {{PromptSafety.ReviewerInputNotice}}
            {{PromptSafety.DataOnlyNotice}}

            SOURCE:
            {{PromptSafety.Wrap(about, "source metadata")}}

            PAGE TEXT (the only evidence you may use):
            {{PromptSafety.Wrap(text, "page text")}}

            Answer each item below about this source. Score 1 (yes), 0.5 (partly) or 0 (no, or the page does not show it).
            For every score above 0, copy a short passage (one or two sentences) word for word from the page text that shows it; without one the score is 0.
            Item 3.3 is asked the other way round: score 1 when there is no sign of a vested interest, and give a quote only when you score it lower.
            Judge only from the page text, not from what you may know about the author or the site.

            ITEMS:
            {{items}}

            Then decide whether the source is an OPINION PIECE: it mainly argues for a view from the writer's own experience or judgement, rather than reporting data, a study or documentation.
            If it is, copy its main claim word for word from the page, and answer these questions about the claim and its writer, each in one sentence with a word-for-word quote from the page:
            - expertise: How credible is the writer as an expert source?
            - field: Is the writer an expert in the field the claim belongs to?
            - assertion: What did the writer assert that implies the claim?
            - evidence: Is the writer's assertion based on evidence (data, examples, measurements, references)?
            If it is not an opinion piece, set isOpinion to false and leave the rest out.

            Respond ONLY with a valid minified JSON object of this shape, with one entry per item, in order:
            {{AnswerShape}}
            """;
        return await LlmJson.GetAsync<ModelQualityAnswers>(chat, prompt, LlmJson.JsonMode(0.0), ModelQualityAnswers.Validate);
    }
}

/// <summary>The model's answer to one checklist item.</summary>
public sealed class ModelQualityAnswer
{
    public string Id { get; set; } = "";
    public double Score { get; set; }
    public string Reason { get; set; } = "";
    public string? Quote { get; set; }
}

/// <summary>The model's answers to the checklist, as JSON.</summary>
public sealed class ModelQualityAnswers
{
    public List<ModelQualityAnswer> Answers { get; set; } = new();

    /// <summary>For an opinion piece, its claim and the answers to four critical questions; older answers have none.</summary>
    public ModelOpinion? Opinion { get; set; }

    /// <summary>Null when the answer is usable: every model item answered once, with a score of 1, 0.5 or 0 and a reason.</summary>
    public static string? Validate(ModelQualityAnswers a)
    {
        var expected = GreyQualityChecklist.ModelItems.Select(i => i.Id).ToList();
        var ids = a.Answers.Select(x => x.Id.Trim()).ToList();
        var missing = expected.Except(ids).ToList();
        if (missing.Count > 0) return $"answers are missing for items {string.Join(", ", missing)}.";
        if (a.Answers.Any(x => x.Score is not (0 or 0.5 or 1))) return "every score must be 1, 0.5 or 0.";
        if (a.Answers.Any(x => string.IsNullOrWhiteSpace(x.Reason))) return "every answer needs a reason.";
        if (a.Opinion is { IsOpinion: true } o)
        {
            if (string.IsNullOrWhiteSpace(o.Claim)) return "an opinion piece needs its main claim, word for word.";
            if (new[] { o.Expertise, o.Field, o.Assertion, o.Evidence }.Any(x => string.IsNullOrWhiteSpace(x?.Answer)))
                return "an opinion piece needs answers to expertise, field, assertion and evidence.";
        }
        return null;
    }
}

/// <summary>The model's reading of an opinion piece: its claim and the answers to four critical questions.</summary>
public sealed class ModelOpinion
{
    public bool IsOpinion { get; set; }
    public string? Claim { get; set; }
    public ModelOpinionAnswer? Expertise { get; set; }
    public ModelOpinionAnswer? Field { get; set; }
    public ModelOpinionAnswer? Assertion { get; set; }
    public ModelOpinionAnswer? Evidence { get; set; }
}

/// <summary>One answer about an opinion piece, with its quote from the page.</summary>
public sealed class ModelOpinionAnswer
{
    public string? Answer { get; set; }
    public string? Quote { get; set; }
}
