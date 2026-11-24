using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The discussion, written from the finished theme subsections.</summary>
public static partial class ReviewWriter
{
    /// <summary>
    /// The discussion, written from the theme subsections, with the research questions when there are any; empty when it
    /// cannot be written. A finding ends with its citation, and an uncited sentence, the review's own reading, is sent
    /// back when it reports a finding (<see cref="UncitedAttribution"/>).
    /// </summary>
    public static async Task<string> WriteDiscussionAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        IReadOnlyList<SynthesisSection> sections, ReviewEvidence evidence, int referenceCount, Action<string>? warn = null,
        IReadOnlyList<string>? questions = null) =>
        (await WriteDiscussionCoreAsync(chat, profile, topic, goal, sections, evidence, referenceCount, warn, questions, answer: false, unanswered: null)).Text;

    /// <summary>
    /// The discussion and, first, the answers to the research questions it builds on, from one call (issue #446). The answers
    /// get the checks the closing step made on them (<see cref="AnswersProblem"/>); a question no evidence bears on
    /// (<paramref name="unanswered"/>, e.g. "RQ2") is answered in code. Without questions the prompt is exactly the one
    /// <see cref="WriteDiscussionAsync"/> sends, and the answers are null.
    /// </summary>
    public static Task<DiscussionWithAnswers> WriteDiscussionAndAnswersAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        IReadOnlyList<SynthesisSection> sections, ReviewEvidence evidence, int referenceCount, Action<string>? warn,
        IReadOnlyList<string>? questions, IReadOnlyCollection<string>? unanswered) =>
        WriteDiscussionCoreAsync(chat, profile, topic, goal, sections, evidence, referenceCount, warn, questions, answer: true, unanswered);

    private static async Task<DiscussionWithAnswers> WriteDiscussionCoreAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        IReadOnlyList<SynthesisSection> sections, ReviewEvidence evidence, int referenceCount, Action<string>? warn,
        IReadOnlyList<string>? questions, bool answer, IReadOnlyCollection<string>? unanswered)
    {
        var all = questions ?? Array.Empty<string>();
        var asked = answer ? AnsweredByModel(all, unanswered) : new List<(string Id, string Text)>();
        string answersField = asked.Count == 0 ? "" : $"\"answers\":[\"answer to {asked[0].Id}\"],";
        int paragraphs = Math.Clamp(2 + sections.Count / 2, 3, 6);
        string synthesis = string.Join("\n\n", sections.Select(x => $"### {x.Heading}\n{x.Text}"));
        string prompt = $$"""
            You are writing {{profile.DiscussionSection}} of {{profile.Review}} whose results are reported theme by theme below.

            {{AimBlock(goal, questions)}}
            PRIMARY TOPIC: "{{topic}}"
            {{PromptSafety.ReviewerInputNotice}}

            REFERENCE LIST (cite ONLY these numbers):
            {{evidence.ReferenceLines()}}

            FACTS ABOUT THE EVIDENCE BASE ({{profile.BaseFactsFrom}}; use them wherever you describe the {{profile.Sources}} or how they were studied, and do not change them):
            {{evidence.BaseFacts}}{{LeftOutBlock(evidence, profile)}}

            RESULTS BY THEME:
            {{synthesis}}

            REQUIREMENTS:
            1. Write about {{paragraphs}} paragraphs, separated by a blank line: {{profile.DiscussionParts}} {{EachPartOnceRule}}
            2. Interpret, do not repeat the results. Ground every claim in the results above. {{EvidenceThenReadingRule(profile)}} {{ClaimPerCitationRule(profile)}} {{OwnCountRule}}
            3. Do not invent numbers, statistics or {{profile.Sources}}.
            4. {{ReadingBasisRule(profile)}}{{AnswersRule(all, asked)}}
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {{{answersField}}"text":"the discussion with inline [n] citations"}
            """;
        try
        {
            var reply = await LlmJson.GetAsync<DiscussionAnswer>(chat, prompt, LlmJson.JsonMode(0.3), WithFigureGuard<DiscussionAnswer>(a =>
            {
                if (string.IsNullOrWhiteSpace(a.Text)) return "text must not be empty.";
                var outside = ThematicSynthesis.CitedIn(a.Text).Where(r => r < 1 || r > referenceCount).ToList();
                if (outside.Count > 0) return $"citation numbers {string.Join(", ", outside)} are not in the reference list.";
                if (LeftOutCited(a.Text, evidence) is { } leftOut) return leftOut;
                if (ReadingBasisSentence(a.Text) is { } basis) return ReadingBasisProblem(basis, profile);
                if (UncitedAttribution(a.Text, evidence.BaseFacts) is { } reported) return reported;
                if (OwnCountCited(a.Text, evidence.BaseFacts, synthesis) is { } own)
                    return $"\"{own}\" states this review's own count and carries a citation; the review's own counts take no citation.";
                return asked.Count == 0 ? null : AnswersProblem(a.Answers, asked.Count, evidence, synthesis, referenceCount, profile);
            }, a => FigurePlacementProblem(synthesis, evidence.BaseFacts,
                (a.Answers ?? new()).Select((t, i) => ($"answer {i + 1}", t ?? "")).Append(("the discussion", a.Text!)).ToList()), "discussion call", warn), repairAttempts: 2);
            string text = OneLimitationsParagraph(MethodsSectionWriter.StripMarkdownEmphasis(reply.Text!.Trim()), out string? merged);
            if (merged != null) warn?.Invoke(merged);
            return new DiscussionWithAnswers(text, answer && all.Count > 0 ? AllAnswers(all, asked, reply.Answers ?? new(), profile) : null);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Discussion could not be written: {ex.Message}");
            return new DiscussionWithAnswers("", null);
        }
    }

    /// <summary>
    /// The prompt rule that asks for each part of the discussion once (issue #248): in a live run the last two
    /// paragraphs both described the limitations in nearly the same words.
    /// </summary>
    public const string EachPartOnceRule =
        "Write each part once, in the order given: a part may take more than one paragraph, but no later paragraph comes back to a part already written or says its points again.";

    private static readonly Regex LimitationsOpening = new(@"\b(?:limitations?|caveats?|shortcomings?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The discussion with its limitations in one paragraph (issue #248). A paragraph whose first sentence speaks of
    /// limitations is a limitations paragraph; when there are several, they become one, in the place of the first,
    /// and a sentence that says again what an earlier one said (most of its words are the same, and it cites nothing the
    /// earlier one does not) is left out, so no citation is lost. Nothing
    /// else changes. <paramref name="note"/> says what was merged and dropped, for the run's files, or is null when
    /// nothing was. Public for unit tests.
    /// </summary>
    public static string OneLimitationsParagraph(string text, out string? note)
    {
        note = null;
        var paragraphs = Regex.Split(text.Trim(), @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var limits = paragraphs.Select((p, i) => (p, i))
            .Where(x => CitationSupportChecker.SplitSentences(x.p).FirstOrDefault() is { } first && LimitationsOpening.IsMatch(first))
            .Select(x => x.i).ToList();
        if (limits.Count < 2) return text;

        var kept = CitationSupportChecker.SplitSentences(paragraphs[limits[0]]).ToList();
        var dropped = new List<string>();
        foreach (var sentence in limits.Skip(1).SelectMany(i => CitationSupportChecker.SplitSentences(paragraphs[i])))
            (kept.Any(k => SaysAgain(sentence, k) && ThematicSynthesis.CitedIn(sentence).All(ThematicSynthesis.CitedIn(k).Contains)) ? dropped : kept).Add(sentence);
        paragraphs[limits[0]] = string.Join(" ", kept);
        foreach (int i in limits.Skip(1).Reverse()) paragraphs.RemoveAt(i);
        note = $"The discussion described the limitations in {limits.Count} paragraphs; they were merged into one in code" +
               (dropped.Count == 0 ? "." : $", and {dropped.Count} sentence{(dropped.Count == 1 ? "" : "s")} that said again what an earlier one said {(dropped.Count == 1 ? "was" : "were")} left out: " +
                string.Join(" ", dropped.Select(d => $"\"{d}\"")));
        return string.Join("\n\n", paragraphs);
    }

    private static readonly HashSet<string> RepeatIgnoredWords = new(StringComparer.Ordinal)
        { "this", "that", "these", "those", "which", "with", "from", "were", "have", "been", "their", "they", "also", "more", "most", "such", "into", "than", "only", "both", "each", "some" };

    /// <summary>
    /// Whether a sentence says again what an earlier one said: of their words of four letters or more (common words and
    /// citation markers left out), the shared ones are at least 60 % of the shorter sentence's and half of the longer's.
    /// </summary>
    private static bool SaysAgain(string sentence, string earlier)
    {
        static HashSet<string> Words(string s) => Regex.Matches(Regex.Replace(s, @"\[[^\]]*\]", ""), @"\p{L}{4,}")
            .Select(m => m.Value.ToLowerInvariant()).Where(w => !RepeatIgnoredWords.Contains(w)).ToHashSet();
        var a = Words(sentence);
        var b = Words(earlier);
        if (a.Count == 0 || b.Count == 0) return false;
        int shared = a.Count(b.Contains);
        return shared * 5 >= Math.Min(a.Count, b.Count) * 3 && shared * 2 >= Math.Max(a.Count, b.Count);
    }

    /// <summary>
    /// The sources the synthesis left out of every theme, for the discussion and the closing sections (issue #166), so
    /// they do not lean on a study the review itself set aside. Empty when none was left out, so the prompt is as before.
    /// </summary>
    public static string LeftOutBlock(ReviewEvidence evidence, WritingProfile profile) => evidence.LeftOut.Count == 0 ? "" : $"""


        {profile.Sources.ToUpperInvariant()} LEFT OUT OF THE THEMES (in the reference list, but the synthesis did not use them; do not cite them as evidence for any claim):
        {string.Join("\n", evidence.LeftOut.Select(u => $"[{u.Reference}] {u.Reason}"))}
        """;

    /// <summary>
    /// Why a text that cites a source left out of the themes cannot be used, or null (issue #166). Public for unit tests.
    /// </summary>
    public static string? LeftOutCited(string text, ReviewEvidence evidence)
    {
        if (evidence.LeftOut.Count == 0) return null;
        var cited = ThematicSynthesis.CitedIn(text);
        var used = evidence.LeftOut.Where(u => cited.Contains(u.Reference)).Select(u => $"[{u.Reference}]").ToList();
        return used.Count == 0 ? null
            : $"it cites {string.Join(", ", used)}, which the synthesis left out of the themes; do not use {(used.Count == 1 ? "it" : "them")} as evidence.";
    }

    /// <summary>
    /// The prompt rule on what the review read each source from (issue #167): that is the review's reading, not the
    /// source's method, and how many were read in full is said once, by the limitations the code writes.
    /// </summary>
    public static string ReadingBasisRule(WritingProfile profile) =>
        $"What a {profile.Source} was read from (its full text or only its abstract) is what this review read, not part of the {profile.Source}'s method: never describe it as the {profile.Source}'s method or design, and do not say how many {profile.Sources} were read in full text or from the abstract only. The limitations of this review say that once; do not repeat it.";

    private static readonly Regex ReadingBasis = new(
        @"\b(?:full[- ]?texts?|abstracts?)\s+(?:was|were|is|are)\s+(?:only\s+)?(?:read|available|retrieved|obtained|used)\b" +
        @"|\b(?:read|retrieved|assessed|evaluated|appraised|analy[sz]ed|examined|reviewed)\s+(?:(?:only|solely|just)\s+)?(?:in\s+full\b|(?:through|from|via|using|on)\s+(?:the\s+|their\s+|its\s+)?(?:full[- ]?texts?|abstracts?)\b)" +
        @"|\bfull[- ]?text\s+(?:analysis|reading)\b|\babstracts?[- ]only\b|\bonly\s+(?:the\s+|their\s+|its\s+)?abstracts?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The first sentence without a citation that says what this review read its sources from, or null (issue #167). A
    /// cited sentence is a source's claim, for example about full-text screening, and is left alone. Public for unit tests.
    /// </summary>
    public static string? ReadingBasisSentence(string text) =>
        CitationSupportChecker.SplitSentences(text ?? "").FirstOrDefault(s => ThematicSynthesis.CitedIn(s).Count == 0 && ReadingBasis.IsMatch(s));

    /// <summary>The message that sends back a text with such a sentence.</summary>
    public static string ReadingBasisProblem(string sentence, WritingProfile profile) =>
        $"\"{sentence.Trim()}\" says what this review read the {profile.Sources} from; the limitations of this review say that once, so leave it out, and never present it as a {profile.Source}'s method.";
}
