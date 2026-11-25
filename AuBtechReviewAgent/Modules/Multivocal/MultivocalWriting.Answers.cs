using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// The answers to the research questions, the discussion that builds on them, and the parts after it (issues #460, #461):
// the shared answers-first path of the systematic review, with the questions numbered as the plan numbers them.
public sealed partial class MultivocalWriter
{
    /// <summary>What the paper says, in code, in place of a discussion that failed its checks on every try (issue #460).</summary>
    public const string DiscussionUnwrittenText =
        "The discussion could not be written: the language model's answer failed the checks in code on every try. " +
        "The answers to the research questions it writes, the limitations of the evidence, the implications and the conclusion are missing for the same reason. " +
        "The results above, the answers counted in code and the limitations of the review written in code stand as they are.";

    /// <summary>The answer, in code, to a question the model answers with the discussion when the discussion could not be written.</summary>
    public const string AnswerUnwrittenText =
        "The answer to this question is written with the discussion, which could not be written; the theme sections on it are in the results.";

    /// <summary>The answer, in code, to a question no extracted source states anything about.</summary>
    public const string NoSourceText =
        "No extracted source states anything this question asks about, so the review cannot answer it from these sources.";

    /// <summary>The answer, in code, to a question whose findings formed no theme that could be written up.</summary>
    public const string NoThemeText =
        "The sources' findings on this question formed no theme that could be written up, so the review does not answer it.";

    /// <summary>How many times the discussion call is made before the paper says, in code, that it could not be written.</summary>
    public const int DiscussionTries = 2;

    /// <summary>The ids and texts of the plan's questions, in order, for the shared writing steps.</summary>
    public static (List<string> Ids, List<string> Texts) Questions(MultivocalPlan plan)
    {
        var numbered = plan.Numbered();
        return (numbered.Select(q => q.Number).ToList(), numbered.Select(q => q.Question.Text.Trim()).ToList());
    }

    /// <summary>The questions the model does not answer: those answered in code, and those with no written theme section.</summary>
    public static HashSet<string> AnsweredWithoutModel(MultivocalPlan plan, IReadOnlyList<PaperResult> results) =>
        plan.Numbered().Select(q => q.Number).Where(n => !results.Any(r => r.Question == n && r.Kind == "theme")).ToHashSet();

    /// <summary>
    /// The discussion with the answers first, from one call (issue #446), tried again once when it fails its checks: a
    /// discussion that fails is never left out unseen (issue #460).
    /// </summary>
    private static async Task<DiscussionWithAnswers> DiscussAsync(IChatCompletionService chat, WritingProfile profile, MultivocalPlan plan, string goal,
        IReadOnlyList<SynthesisSection> results, ReviewEvidence evidence, int referenceCount, IReadOnlyCollection<string> unanswered, Action<string> warn)
    {
        var (_, texts) = Questions(plan);
        var written = new DiscussionWithAnswers("", null);
        for (int attempt = 1; attempt <= DiscussionTries && written.Text.Length == 0; attempt++)
        {
            if (attempt > 1) warn("The discussion is written again, once, after its answer failed the checks.");
            using (LlmStage.Begin("discussion"))
                written = await ReviewWriter.WriteDiscussionAndAnswersAsync(chat, profile, plan.Topic, goal, results, evidence, referenceCount, warn, texts, unanswered);
        }
        return written;
    }

    /// <summary>
    /// Every question's answer in the plan's order: the model's from the discussion call for the questions it answers, and
    /// the one written in code for the others (counted, no source, no theme, or the discussion failed).
    /// </summary>
    public static List<QuestionAnswer> Answers(MultivocalReportInput input, IReadOnlyList<PaperResult> results, IReadOnlyList<string>? written)
    {
        var plan = input.Planned.Plan;
        var without = AnsweredWithoutModel(plan, results);
        return plan.Numbered().Select((q, i) =>
        {
            var answer = new QuestionAnswer { Question = q.Number, QuestionText = q.Question.Text.Trim() };
            if (MultivocalPaperParts.AnsweredInCode(q.Question.Type)) answer.Text = MultivocalPaperParts.CodeAnswerSummary(input, q.Number);
            else if (without.Contains(q.Number)) answer.Text = input.Synthesis.Unanswered.Contains(q.Number) ? NoSourceText : NoThemeText;
            else if (written is { } w && i < w.Count && !string.IsNullOrWhiteSpace(w[i])) (answer.Kind, answer.Text) = ("written", w[i]);
            else answer.Text = AnswerUnwrittenText;
            return answer;
        }).ToList();
    }

    /// <summary>
    /// The limitations of the evidence, the implications for practitioners, the directions for researchers and the
    /// conclusion, written after the discussion through the shared closing step with the answers as given text.
    /// </summary>
    private static async Task CloseAsync(IChatCompletionService chat, WritingProfile profile, MultivocalReportInput input, MultivocalPaperFile file, string goal,
        IReadOnlyList<SynthesisSection> results, ReviewEvidence evidence, int referenceCount, int sectionsWritten, Action<string> warn)
    {
        var plan = input.Planned.Plan;
        var (_, texts) = Questions(plan);
        var closing = await ReviewWriter.WriteClosingAsync(chat, profile, plan.Topic, goal, FrontMatterFacts(input, sectionsWritten), results, file.Discussion,
            evidence, referenceCount, warn, texts, AnsweredWithoutModel(plan, file.Results), file.Answers.Select(a => a.Text).ToList());
        if (closing == null) return;
        (file.Limitations, file.Implications, file.FutureResearch, file.Conclusion) = (closing.EvidenceLimitations, closing.Implications, closing.FutureResearch, closing.Conclusion);
    }
}
