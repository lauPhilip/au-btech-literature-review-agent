using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>The discussion with the answers to the research questions it builds on, both from one call.</summary>
/// <param name="Text">The discussion; empty when it could not be written.</param>
/// <param name="Answers">One answer per research question, in order (the ones no evidence bears on written in code), or null without questions or when the call failed.</param>
public sealed record DiscussionWithAnswers(string Text, IReadOnlyList<string>? Answers);

/// <summary>
/// Answers first (issue #446). The abstract, the answers and the conclusion were written by parallel calls that
/// did not see each other, so a review stated its main reading three ways. Now the discussion call writes the answers to
/// the research questions first and builds on them, and the closing sections and the front matter, written after it, get
/// the answers as given text. No call is added.
/// </summary>
public static partial class ReviewWriter
{
    private sealed class DiscussionAnswer
    {
        public List<string>? Answers { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>The questions the model answers: every one but those no evidence bears on (<paramref name="unanswered"/>, e.g. "RQ2").</summary>
    private static List<(string Id, string Text)> AnsweredByModel(IReadOnlyList<string>? questions, IReadOnlyCollection<string>? unanswered) =>
        (questions ?? Array.Empty<string>()).Select((q, i) => (Id: QuestionId(i), Text: q)).Where(q => unanswered?.Contains(q.Id) != true).ToList();

    /// <summary>The discussion prompt's rule for the answers; empty when the model answers no question.</summary>
    private static string AnswersRule(IReadOnlyList<string> questions, List<(string Id, string Text)> asked)
    {
        if (asked.Count == 0) return "";
        var inCode = questions.Select((_, i) => QuestionId(i)).Except(asked.Select(q => q.Id)).ToList();
        string inCodeLine = inCode.Count == 0 ? "" : $" No evidence bears on {string.Join(", ", inCode)}, so {(inCode.Count == 1 ? "it is" : "they are")} answered in code; give no answer for {(inCode.Count == 1 ? "it" : "them")}.";
        return $"""

            5. Write the answers first. "answers": exactly {asked.Count} answer(s), one for each of {string.Join(", ", asked.Select(q => q.Id))} in that order, each one to three sentences that answer the question from the results, with inline [n] markers. Do not repeat the question. Where the results do not answer a question, say so plainly.{inCodeLine} The answers are printed before the discussion, and the abstract and the conclusion are written from them, so the conclusion must not restate them; the discussion builds on the answers, says what they mean together and where the themes connect, and does not restate them either. A figure from the results (a number, percentage or effect size) is given in at most one answer, and the discussion repeats none that an answer gives.
            """;
    }

    /// <summary>
    /// Why the answers in a discussion answer cannot be used, or null: the same checks the closing step made on them (one
    /// answer per question, no review count with a citation, no uncited finding, nothing on what the review read, no
    /// source left out of the themes, citations in the reference list, numbers only from the results and the facts).
    /// </summary>
    public static string? AnswersProblem(IReadOnlyList<string>? answers, int questions, ReviewEvidence evidence, string results, int referenceCount, WritingProfile profile)
    {
        var given = (answers ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        if (given.Count != questions) return $"answers must hold exactly {questions} answer(s), one per research question; you gave {given.Count}.";
        for (int i = 0; i < given.Count; i++)
        {
            string name = $"answer {i + 1}", text = given[i];
            if (OwnCountCited(text, evidence.BaseFacts, results) is { } own)
                return $"{name}: \"{own}\" states this review's own count and carries a citation; the review's own counts take no citation.";
            if (UncitedAttribution(text, evidence.BaseFacts) is { } reported) return $"{name}: {reported}";
            if (ReadingBasisSentence(text) is { } basis) return $"{name}: {ReadingBasisProblem(basis, profile)}";
            if (LeftOutCited(text, evidence) is { } used) return $"{name}: {used}";
        }
        var outside = given.SelectMany(ThematicSynthesis.CitedIn).Where(r => r < 1 || r > referenceCount).Distinct().ToList();
        if (outside.Count > 0) return $"citation numbers {string.Join(", ", outside)} are not in the reference list.";
        var known = NumbersIn(results + "\n" + evidence.BaseFacts);
        var unknown = given.SelectMany(NumbersIn).Where(n => !known.Contains(n)).Distinct().ToList();
        return unknown.Count > 0 ? $"the answers use {string.Join(", ", unknown)}, which is in neither the results nor the facts; use only their numbers." : null;
    }

    /// <summary>Every question's answer in order: the model's, or the one written in code for a question no evidence bears on.</summary>
    private static List<string> AllAnswers(IReadOnlyList<string> questions, List<(string Id, string Text)> asked, IEnumerable<string> modelAnswers, WritingProfile profile)
    {
        var fromModel = new Queue<string>(modelAnswers.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => MethodsSectionWriter.StripMarkdownEmphasis(a.Trim())));
        return questions.Select((_, i) => asked.Any(q => q.Id == QuestionId(i)) ? fromModel.Dequeue() : UnansweredText(profile)).ToList();
    }

    /// <summary>The given answers for a prompt, one "RQn: answer" line each; empty without answers.</summary>
    public static string AnswerLines(IReadOnlyList<string>? answers) =>
        answers is not { Count: > 0 } ? "" : string.Join("\n", answers.Select((a, i) => $"{QuestionId(i)}: {a}"));
}
