using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// A direct answer to every research question, in code, from the verified findings in the data extraction (issue #561).
/// Run 289a0d3e answered none of its questions: the closing step failed its checks on every try and the answers were
/// dropped with it. A finished paper now always has an answer per question: the model's checked answer when there is
/// one, and otherwise this one, which the paper says was assembled in code. It uses only what is already verified: the
/// themes the codebook placed under the question, and the findings tagged with the question whose quote was found word
/// for word in the study, each with the study's citation. The citation check then reads it like any other answer.
/// </summary>
public static class AnswersFromEvidence
{
    /// <summary>At most this many findings per answer, those with figures first, so the answer stays an answer.</summary>
    public const int MaxFindings = 5;

    /// <summary>The answer to a question no verified finding bears on.</summary>
    public const string NoFindingText =
        "No verified finding in the data extraction (Table A.2) bears on this question, so the review cannot answer it from its evidence.";

    /// <summary>The sentence the paper gives when the answers were assembled in code.</summary>
    public const string AssembledSentence =
        "The answers to the research questions were assembled in code from the verified findings in the data extraction (Table A.2), because the written answers failed validation.";

    private static readonly Regex Figure = new(@"\d+(?:[.,]\d+)?\s*(?:%|percent\b|percentage\s+points?\b|-?fold\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>One answer per question, in order (ids "RQ1", "RQ2", ...).</summary>
    public static List<string> Write(IReadOnlyList<string> questions, ThematicCodebook? book, IReadOnlyList<StudyExtraction> extractions)
    {
        var answers = new List<string>();
        for (int i = 0; i < questions.Count; i++)
        {
            string id = $"RQ{i + 1}";
            if (book?.Unanswered?.Any(u => Same(u.Question, id)) == true)
            {
                answers.Add(ReviewWriter.UnansweredText(WritingProfile.Systematic));
                continue;
            }
            // The themes under the question; a theme without question tags bears on every question.
            var themes = (book?.Themes ?? new()).Where(t => t.Questions is not { Count: > 0 } q || q.Any(x => Same(x, id))).ToList();
            var studies = book == null ? null : themes.SelectMany(t => t.Studies).ToHashSet();
            var findings = extractions
                .Where(e => studies == null || studies.Contains(e.ReferenceNumber))
                .SelectMany(e => ThematicSynthesis.VerifiedFindings(e).Select(f => (Reference: e.ReferenceNumber, f.Finding)))
                .Where(f => string.IsNullOrWhiteSpace(f.Finding.Question) ? questions.Count == 1 : Same(f.Finding.Question, id))
                .OrderByDescending(f => Figure.IsMatch(f.Finding.Value))
                .ThenBy(f => f.Reference)
                .Take(MaxFindings)
                .ToList();
            if (findings.Count == 0)
            {
                answers.Add(NoFindingText);
                continue;
            }
            string themeLine = themes.Count == 0 ? "" : themes.Count == 1
                ? $"The verified findings on this question are in the theme \"{themes[0].Name.Trim()}\". "
                : $"The verified findings on this question are in the themes {string.Join(", ", themes.Take(themes.Count - 1).Select(t => $"\"{t.Name.Trim()}\""))} and \"{themes[^1].Name.Trim()}\". ";
            answers.Add(themeLine + string.Join(" ", findings.Select(f => $"{Sentence(f.Finding.Value)} [{f.Reference}].")));
        }
        return answers;
    }

    private static bool Same(string? a, string id) => a?.Trim().Equals(id, StringComparison.OrdinalIgnoreCase) == true;

    // A finding's value as a sentence: no brackets (they would read as citations), a capital first letter, no end stop.
    private static string Sentence(string value)
    {
        string text = Regex.Replace(value.Replace('[', '(').Replace(']', ')'), @"\s+", " ").Trim().TrimEnd('.', ';', ':', ',');
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
