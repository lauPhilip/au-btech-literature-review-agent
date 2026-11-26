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
    /// <summary>
    /// The statement for the methods section (several sentences). With the reviewer's edits of the report, it says how
    /// many sentences were edited and how their citations were checked again; without, it reads as before. With the
    /// run's artifact, it says that the model chose its content and code drew it.
    /// </summary>
    public static string Methods(ReviewState state, Guid runId, ReportEditLog? edits = null, ReviewArtifact? artifact = null)
    {
        var s = state.Stats;
        var inv = CultureInfo.InvariantCulture;
        string model = string.IsNullOrWhiteSpace(state.RunSettings?.Model) ? "a large language model" : $"the large language model {state.RunSettings!.Model}";
        string? fast = state.RunSettings?.FastModel;
        if (!string.IsNullOrWhiteSpace(fast)) model += $" (and {fast} for screening and citation checks)";
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
        text.Append(LanguageEdit("abstract and introduction"));
        text.Append(ArtifactSentence(artifact));
        if (state.SeedChaining is { Seeds.Count: > 0 })
            text.Append("The seed studies for citation chaining were chosen in code from the citation counts in the databases' metadata, not by the model. ");

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
        // A paper that only lost invisible characters is on file too (#369), but it held nothing that reads like instructions.
        // Flags from an older run's scan are judged again with the current patterns (#478), so an old name is not taken as strong.
        var withHits = state.FullTextFlags?.Select(f => f with { Flags = PromptSafety.Current(f.Flags, f.Scan) ?? new() })
            .Where(f => f.Flags.Count > 0).ToList() ?? new();
        int flagged = withHits.Where(f => PromptSafety.AnyStrong(f.Flags)).Select(f => f.PaperId).Distinct().Count();
        int mentioned = withHits.Select(f => f.PaperId).Distinct().Count() - flagged;
        text.Append(FlaggedSources(flagged, s.FullTextRetrieved, "full text", "included study", "included studies", mentioned));
        if (edits?.Sentence() is { Length: > 0 } edited) text.Append(edited).Append(' ');
        text.Append("The complete record of the run (protocol, search responses, every screening decision and model call, and the citation audit) is available as a fingerprinted archive. ");
        text.Append(PaperDeclarations.Responsibility);
        return text.ToString();
    }

    /// <summary>A short version for acknowledgements or a declaration of AI use. It does not claim that anyone checked the output.</summary>
    public static string Short(ReviewState state) =>
        $"The literature search, screening, data extraction, a first draft of the synthesis and a language edit of the abstract and introduction were carried out with TraceableAI using {Models(state)}; " +
        "every citation was automatically checked against the cited paper. " + PaperDeclarations.Responsibility;

    /// <summary>The tool and model, for Elsevier's declaration ("the authors used ... in order to ...").</summary>
    public static string Used(ReviewState state)
    {
        string version = string.IsNullOrWhiteSpace(state.RunSettings?.AppVersion) ? "" : $" (version {state.RunSettings!.AppVersion.Split('+')[0]})";
        return $"TraceableAI{version}, an open-source review tool, with {Models(state)}";
    }

    /// <summary>What the model was used for, for Elsevier's declaration.</summary>
    public const string Purpose = "generate search phrasings, screen records, extract and appraise study data, draft the synthesis, check each citation against the cited paper and edit the language of the abstract and introduction";

    /// <summary>
    /// The sentence on the language edit (once called the stylistic pass): the model rewrote the named parts for clear,
    /// direct academic prose, and code kept a rewrite only when no number or citation changed. Shared with the multivocal paper.
    /// </summary>
    public static string LanguageEdit(string parts) =>
        $"The {parts} were then given a language edit by the model, for clear and direct prose; code kept an edit only when it changed no number and no citation, and the text before and after each edit is on file. ";

    /// <summary>Who chose the artifact's content and what drew it; empty when the run built none.</summary>
    public static string ArtifactSentence(ReviewArtifact? artifact)
    {
        if (artifact is not { Error: null }) return "";
        string name = artifact.Kind switch
        {
            ArtifactKinds.ConceptMap => "concept map",
            ArtifactKinds.Flowchart => "flowchart",
            ArtifactKinds.Table => "comparison table",
            ArtifactKinds.Bullets => "bullet list",
            _ => "",
        };
        if (name.Length == 0) return "";
        return artifact.IsDiagram
            ? $"The content of the {name} (its boxes and the labelled links between them) was chosen by the model from the findings and drawn in code; every cited box was checked like a sentence of the report" +
              (ArtifactLinks.MethodsSentence(artifact).Length == 0 ? ". "
                  : ", and every link was checked against the studies that both of its boxes cite; a link none of them states is drawn dashed as the review's own reading. ")
            : $"The content of the {name} was chosen by the model from the findings and laid out in code; every cited entry was checked like a sentence of the report. ";
    }

    private static string Models(ReviewState state)
    {
        string model = string.IsNullOrWhiteSpace(state.RunSettings?.Model) ? "a large language model" : state.RunSettings!.Model;
        if (!string.IsNullOrWhiteSpace(state.RunSettings?.FastModel)) model += $" and {state.RunSettings!.FastModel}";
        return model;
    }

    /// <summary>
    /// The sentences on source texts that hold phrases aimed at a model (#363, #472), ending with a space: from the number
    /// of distinct sources with a strong flag, the number whose text was kept, and the number that only had weak ones
    /// (they name prompts or instructions as a subject, which a review about language models does all the time). Empty when
    /// neither, so the statement of a run without flags stays word for word as it was. A flag changes no decision; this says so.
    /// </summary>
    public static string FlaggedSources(int flagged, int kept, string text, string one, string many, int mentioned = 0)
    {
        flagged = Math.Max(flagged, 0);
        mentioned = Math.Max(mentioned, 0);
        if (flagged + mentioned == 0) return "";
        kept = Math.Max(kept, flagged + mentioned);
        string sources = kept == 1 ? one : many;
        if (flagged == 0)
            return string.Create(CultureInfo.InvariantCulture,
                $"The {text} of {mentioned} of {kept} {sources} named prompts or model instructions as a subject, but none held a phrase addressed to a model as an instruction; these mentions are kept in the run's record and changed no decision. ");
        string sentence = string.Create(CultureInfo.InvariantCulture,
            $"The {text} of {flagged} of {kept} {sources} contained phrases addressed to a model as instructions; {(flagged == 1 ? "it was" : "they were")} flagged for the reviewer and changed no decision. ");
        return mentioned == 0 ? sentence : sentence + string.Create(CultureInfo.InvariantCulture,
            $"The {text} of {mentioned} more only named prompts or model instructions as a subject; these mentions are kept in the run's record but were not flagged. ");
    }

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
