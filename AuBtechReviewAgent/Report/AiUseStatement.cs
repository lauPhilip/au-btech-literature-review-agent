using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// A ready-made statement on the use of AI for a paper or thesis that uses a TraceableAI review: which model,
/// which steps it did, what a person checked, and how well the citations held up. Built only from what the
/// run recorded, so it never claims a step that did not happen; the reviewer still edits it for their venue.
/// </summary>
public static class AiUseStatement
{
    /// <summary>The statement for the methods section (several sentences).</summary>
    public static string Methods(ReviewState state, Guid runId)
    {
        var s = state.Stats;
        var inv = CultureInfo.InvariantCulture;
        string model = string.IsNullOrWhiteSpace(state.RunSettings?.Model) ? "a large language model" : $"the large language model {state.RunSettings!.Model}";
        string version = string.IsNullOrWhiteSpace(state.RunSettings?.AppVersion) ? "" : $" (version {state.RunSettings!.AppVersion.Split('+')[0]})";
        string date = (state.CompletedUtc ?? state.Timestamp).ToString("d MMMM yyyy", inv);

        var text = new StringBuilder();
        text.Append(inv, $"This review was conducted with TraceableAI{version}, an open-source tool that runs PRISMA 2020 systematic reviews with {model}, on {date} (run {runId}). ");
        text.Append("The review protocol (question, eligibility criteria and sources) was written by the authors and fingerprinted before the search. ");

        var automated = new List<string> { "generating additional search phrasings", "screening titles and abstracts against the eligibility criteria" };
        if (state.CitationChainingRequested) automated.Add("screening the references and citing papers of included studies");
        automated.Add("extracting study data and appraising quality with the Mixed Methods Appraisal Tool");
        automated.Add(state.ThematicSynthesis is { Themes.Count: > 0 } ? "coding findings into themes and drafting the synthesis" : "drafting the synthesis");
        automated.Add("checking each citation against the cited paper");
        text.Append("The model was used for ").Append(JoinList(automated)).Append(". ");

        if (state.DualScreeningRequested && s.DualScreened > 0)
        {
            text.Append(inv, $"Every record was screened twice by independent prompts; disagreements were kept and flagged");
            text.Append(s.ScreeningKappa is double k ? string.Create(inv, $" (Cohen's kappa = {k:0.00}). ") : ". ");
        }
        if (state.HumanScreeningReviewRequested && s.HumanReviewed > 0)
            text.Append(inv, $"A human reviewer checked {s.HumanReviewed} screening decisions and changed {s.HumanOverrides} of them. ");
        else
            text.Append("Screening decisions were not checked by a human reviewer within the tool. ");

        if (s.CitationsChecked > 0)
        {
            text.Append(inv, $"Of {s.CitationsChecked} citations in the generated text, {s.CitationsSupported} were judged supported by the cited paper, ");
            text.Append(inv, $"{s.CitationsPartiallySupported} partly supported and {s.CitationsNotSupported} not supported");
            int other = s.CitationsInsufficientEvidence + s.CitationsUnverifiable;
            text.Append(other > 0 ? string.Create(inv, $", and {other} could not be checked") : "");
            text.Append("; a supporting verdict counted only when its quote was found verbatim in the paper. ");
        }
        text.Append("The complete record of the run (protocol, search responses, every screening decision and model call, and the citation audit) is available as a fingerprinted archive. ");
        text.Append("The authors reviewed the output and take full responsibility for the content.");
        return text.ToString();
    }

    /// <summary>A one-sentence version for acknowledgements or a declaration of AI use.</summary>
    public static string Short(ReviewState state)
    {
        string model = string.IsNullOrWhiteSpace(state.RunSettings?.Model) ? "a large language model" : state.RunSettings!.Model;
        return $"The literature search, screening, data extraction and a first draft of the synthesis were carried out with TraceableAI using {model}; " +
               "every citation was automatically checked against the cited paper, and the authors reviewed and edited all output and take full responsibility for it.";
    }

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
