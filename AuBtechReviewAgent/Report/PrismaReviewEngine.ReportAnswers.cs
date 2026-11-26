using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The answers to the research questions of a systematic report, and what the paper says when its closing parts could not
/// be written (issue #561). In run 289a0d3e the closing step failed its checks three times; the answers the discussion
/// call had written and checked were dropped with it, and main.tex went from the Discussion to the Declarations without
/// a word. Now an answer is never dropped once it has passed its checks, every question gets an answer, and a missing
/// part is named in the paper.
/// </summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// The answers the paper prints, one per research question, and whether they were assembled in code: the closing
    /// step's, else those the discussion call wrote and checked (issue #446), else answers from the verified findings
    /// (<see cref="AnswersFromEvidence"/>). Null without research questions.
    /// </summary>
    private static (IReadOnlyList<string>? Answers, bool FromEvidence) AnswersFor(ReviewState state, ThematicResult? thematic, ClosingSections? closing)
    {
        var questions = state.ResearchQuestions ?? new List<string>();
        if (questions.Count == 0) return (null, false);
        if (closing?.Answers is { } written && written.Count == questions.Count) return (written, false);
        if (thematic?.Answers is { } given && given.Count == questions.Count) return (given, false);
        return (AnswersFromEvidence.Write(questions, thematic?.Codebook, state.Extractions ?? new List<StudyExtraction>()), true);
    }

    /// <summary>
    /// What the paper says, in code, in place of the conclusion when the closing parts were not written: why, and where
    /// the answers to the research questions come from. Public for unit tests.
    /// </summary>
    public static string ClosingUnwrittenText(bool attempted, bool hasQuestions, bool answersFromEvidence)
    {
        string why = attempted
            ? "The conclusion, the limitations of the evidence, the implications and the directions for future research could not be written: the language model's answer failed the checks in code on every try."
            : "The conclusion, the limitations of the evidence, the implications and the directions for future research were not written, because the thematic synthesis they are written from could not be made.";
        string answers = !hasQuestions ? " The results and the discussion above stand as they are."
            : answersFromEvidence ? " " + AnswersFromEvidence.AssembledSentence
            : " The answers to the research questions above were written and checked with the discussion and stand as they are.";
        return why + answers;
    }

    /// <summary>Notes on file, in thematic-codebook.json, why the closing parts or the written answers are missing.</summary>
    private static void NoteMissingClosing(ReportRun run)
    {
        if (run.Closing != null || run.Thematic == null) return;
        lock (run.Thematic.Codebook)
        {
            var notes = run.Thematic.Codebook.WritingNotes ??= new();
            notes.Add("The closing parts were not written; the paper says so in place of the conclusion.");
            if (run.AnswersFromEvidence)
                notes.Add($"The answers to the research questions were assembled in code from the verified findings ({run.Answers?.Count ?? 0} answer(s)), because no written answer passed its checks.");
        }
    }
}
