using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

// The method sections of the report and the paper, one per phase of the guidelines.
public sealed partial class MultivocalReporter
{
    /// <summary>
    /// The method, one section per phase of the guidelines (planning G1–G5, search G6–G8, selection G9–G10, quality
    /// G11, map and extraction G12, synthesis G13), written in code from the run's files. The report and the paper's
    /// method section use the same text.
    /// </summary>
    public static IReadOnlyList<ReportSection> MethodSections(MultivocalReportInput input) => new[]
    {
        Planning(input), Search(input), Selection(input), QualitySection(input), MapAndExtraction(input), SynthesisMethod(input),
    };

    private static ReportSection Planning(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        var section = new ReportSection { Heading = "1. Planning (G1–G5)" };
        int yes = plan.IncludeGrey.Count(a => a == true);
        section.Blocks.Add(ReportBlock.Paragraph(
            $"The review's goal was: {Sentence(plan.Goal)} It was written for {Audience(plan.Audience)}. " +
            $"Whether to include grey literature was decided with the seven questions of the guidelines' Table 4 (G3): {yes} of {MultivocalGuidelines.IncludeGreyQuestions.Count} were answered \"yes\". " +
            $"The protocol was written on {input.Planned.CreatedUtc:yyyy-MM-dd} (UTC), before any search, and its SHA-256 fingerprint is recorded with the plan (G1)."));
        section.Blocks.Add(ReportBlock.Table(new[] { "Question", "Text", "Type (G5)" },
            plan.Numbered().Select(x => new[] { x.Number, x.Question.Text, MultivocalGuidelines.FindType(x.Question.Type)?.Name ?? "not set" })));
        return section;
    }

    private static ReportSection Search(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        var ledger = input.Ledger;
        var section = new ReportSection { Heading = "2. Search (G6–G8)" };
        // The grey pool's searches; a formal pool's follow in their own paragraph (block I-6).
        var ran = ledger.Searches.Where(s => s.Error == null && !s.Formal).ToList();
        string kinds = Join(plan.GreyTypes.Select(KindLabel).Select(Lower), "; ");
        // The kinds the searches actually found, in the guidelines' order, so the method never claims a kind no search reached.
        var foundKinds = ledger.GreyPool.Select(x => x.Record.Kind).Distinct()
            .OrderBy(k => MultivocalGuidelines.GreyTypes.ToList().FindIndex(t => t.Key == k) is var i && i < 0 ? int.MaxValue : i).ToList();
        string covered = foundKinds.Count == 0 || foundKinds.ToHashSet().SetEquals(plan.GreyTypes)
            ? $"The review covered {kinds} (G6)."
            : $"The protocol named {kinds} as the kinds of grey literature to cover (G6); the sources the searches found were {Join(foundKinds.Select(KindLabel).Select(Lower), "; ")}.";
        section.Blocks.Add(ReportBlock.Paragraph(
            $"{covered} On {ledger.SearchedUtc:yyyy-MM-dd} (UTC), {Count(ran.Count, "search", "searches")} ran across {Count(ran.Select(s => s.Source).Distinct().Count(), "source")} " +
            $"with {Count(plan.EffectiveGreySearchStrings.Count, "search string")} ({Join(plan.EffectiveGreySearchStrings.Select(t => $"\"{t}\""))}) and found {Count(ledger.GreyPool.Count(), "source")} (G7). " +
            $"{MultivocalFormalText.RawAnswersSentence(ledger.Searches.Where(s => !s.Formal))} Stopping rule (G8): {Sentence(ledger.StoppingApplied)}"));
        section.Blocks.Add(ReportBlock.Table(new[] { "Source", "Search string", "Returned", "New" },
            ledger.Searches.Where(s => !s.Formal).Select(s => new[] { MultivocalGuidelines.SearchLabel(s.Source), s.SearchString, s.Error == null ? Num(s.Returned) : $"failed: {s.Error}", Num(s.New) })));
        if (ledger.HasFormalPool) section.Blocks.AddRange(MultivocalFormalText.SearchBlocks(input));
        var notDone = new List<string>();
        notDone.AddRange(ledger.Skipped.Select(s => $"{MultivocalFormalText.SearchName(s.Source)}: not run ({Lower(s.Reason.TrimEnd('.'))})."));
        notDone.Add("No general web search engine was used: none offers a search API that is free and allows its results to be kept, which a traceable review needs.");
        notDone.Add("Practitioners and authors were not contacted for sources; only sources found by the searches were reviewed.");
        section.Blocks.Add(ReportBlock.Paragraph("Not done, and why:"));
        section.Blocks.Add(ReportBlock.List(notDone));
        return section;
    }

    private static ReportSection Selection(MultivocalReportInput input)
    {
        var s = input.Screening;
        var section = new ReportSection { Heading = "3. Selection (G9, G10)" };
        int reviewed = s.Sources.Count(x => x.Reviewed);
        int changed = s.Sources.Count(x => x.Reviewed && x.ModelDecision != null && x.ModelDecision != x.Decision);
        section.Blocks.Add(ReportBlock.Paragraph(
            MultivocalScreeningText.Method(s) +
            $"{Num(s.Count("Included"))} were included, {Num(s.Count("Excluded"))} excluded and {Num(s.Count("Duplicate"))} set aside as duplicates of a source with the same title" +
            $"{(s.Count("Error") > 0 ? $"; {Num(s.Count("Error"))} could not be screened" : "")}. " +
            $"The reviewer looked at {Count(reviewed, "decision")} and changed {Num(changed)}; the model's decision stays on file next to the reviewer's."));
        if (input.Ledger.HasFormalPool) section.Blocks.Add(ReportBlock.Paragraph(MultivocalFormalText.SelectionText(input)));
        section.Blocks.Add(ReportBlock.Paragraph($"Inclusion criteria: {Sentence(s.InclusionCriteria)} Exclusion criteria: {(s.ExclusionCriteria.Trim().Length > 0 ? Sentence(s.ExclusionCriteria) : "none.")}"));
        var reasons = s.Sources.Where(x => x.Decision == "Excluded")
            .GroupBy(x => ReasonLabel(x.ExclusionReason))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new[] { g.Key, Num(g.Count()) }).ToList();
        if (reasons.Count > 0) section.Blocks.Add(ReportBlock.Table(new[] { "Reason for exclusion", "Sources" }, reasons));
        return section;
    }

    private static ReportSection QualitySection(MultivocalReportInput input)
    {
        var q = input.Quality;
        var section = new ReportSection { Heading = "4. Quality (G11)" };
        // Table 7 scores the grey sources; a formal pool's MMAT appraisal has its own paragraph (block I-6).
        var grey = q.Sources.Where(x => !x.Formal).ToList();
        var passed = grey.Where(x => x.Outcome == "Passed").ToList();
        int below = grey.Count(x => x.Outcome == "Below threshold"), notAssessed = grey.Count(x => x.Outcome == "Not assessed");
        int opinions = grey.Count(x => x.Argument != null);
        section.Blocks.Add(ReportBlock.Paragraph(
            $"Every included {(input.Ledger.HasFormalPool ? "grey " : "")}source was scored on the {q.MaxPoints} items of the quality checklist in the guidelines' Table 7, each 1, 0.5 or 0 points, with a threshold of {q.Threshold} points. " +
            $"The language model answered the items that need reading the source, and every point it gave rests on a quote found in the kept text of the source's page; the date, the impact and the outlet type were decided in code (impact: {Lower(q.ImpactRule.TrimEnd('.'))}). " +
            $"{Num(passed.Count)} sources passed, {Num(below)} scored below the threshold and {Num(notAssessed)} could not be assessed{(notAssessed > 0 ? ", most often because their page could not be read" : "")}." +
            GreyPageText.QualityMethod(grey) +
            (opinions > 0 ? $" {Count(opinions, "source")} {(opinions == 1 ? "was" : "were")} recorded as an opinion piece, with the critical questions for an expert opinion (G13)." : "")));
        if (passed.Count > 0)
            section.Blocks.Add(ReportBlock.Paragraph(
                $"Of the sources that passed, {TierCount(passed, input, 1)} came from 1st-tier outlets, {TierCount(passed, input, 2)} from 2nd-tier and {TierCount(passed, input, 3)} from 3rd-tier outlets (checklist item 8.1); " +
                $"their mean score was {passed.Average(x => x.Points).ToString("0.#", CultureInfo.InvariantCulture)} of {q.MaxPoints} points."));
        if (input.Ledger.HasFormalPool) section.Blocks.Add(ReportBlock.Paragraph(MultivocalFormalText.QualityText(input)));
        return section;
    }

    private static int TierCount(IEnumerable<GreyQuality> sources, MultivocalReportInput input, int tier)
    {
        var kinds = input.Ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Record.Kind);
        return sources.Count(s => MultivocalSynthesiser.TierOf(kinds.GetValueOrDefault(s.Address, "")) == tier);
    }

    private static ReportSection MapAndExtraction(MultivocalReportInput input)
    {
        var map = input.Map;
        var section = new ReportSection { Heading = "5. Systematic map and extraction (G12)" };
        var fixedMap = map.Versions.FirstOrDefault(v => v.Number == map.FixedVersion) ?? map.Latest;
        int edited = map.Versions.Count(v => v.By == "reviewer");
        int extracted = input.Extraction.Sources.Count(s => s.Error == null);
        int failed = input.Extraction.Sources.Count - extracted;
        section.Blocks.Add(ReportBlock.Paragraph(
            $"The language model proposed the attributes of the systematic map from the research questions, with values generalised over {Count(map.SourcesSeen, "source")} that passed the quality check. " +
            $"The reviewer {(edited > 0 ? $"edited it {Count(edited, "time")}" : "kept it as proposed")}, and version {fixedMap.Number} was fixed before any source was extracted. " +
            $"{Num(extracted)} sources were extracted against it{(failed > 0 ? $" and {Num(failed)} could not be" : "")}; every value rests on a quote found in the kept text of the source's page, and what a page does not state was recorded as not stated." +
            GreyPageText.ExtractionMethod(input.Extraction) +
            " The kind of each source, whether it is grey or formal literature, its site, date and quality points were recorded in code." +
            (input.Extraction.NameMerges.Count + input.Extraction.OwnSourceValues.Count > 0
                ? $" For the open attributes, names were merged in code only where they match when case, punctuation, spaces and hyphens are ignored, or where one is given in brackets after the other ({Count(input.Extraction.NameMerges.Count, "merge")}), and {Count(input.Extraction.OwnSourceValues.Count, "value")} naming one of the review's own sources {(input.Extraction.OwnSourceValues.Count == 1 ? "was" : "were")} not counted; the data appendix lists both."
                : "") +
            (input.Ledger.HasFormalPool ? MultivocalFormalText.ExtractionText(input) : "")));
        section.Blocks.Add(ReportBlock.Table(new[] { "Attribute", "Question", "Values" },
            fixedMap.Attributes.Select(a => new[] { a.Name, a.Question, a.Open ? "open (named by each source)" : string.Join(", ", a.Values) })));
        return section;
    }

    private static ReportSection SynthesisMethod(MultivocalReportInput input)
    {
        var s = input.Synthesis;
        var section = new ReportSection { Heading = "6. Synthesis (G13)" };
        // Only what the paper shows is counted (#462): a question answered by counting has no themes and no second placement.
        var shown = MultivocalPaperParts.ShownThemes(input);
        var themed = shown.Select(t => t.Question).ToHashSet();
        var placements = s.SecondPlacements.Where(a => a.Kappa != null && themed.Contains(a.Question)).ToList();
        int notCoded = s.NotCoded.Sum(n => n.Findings.Count);
        section.Blocks.Add(ReportBlock.Paragraph(
            $"Each extracted value with its quote became a finding; {Count(s.Findings.Count, "finding")} remained after every quote was checked again against the kept page text, never the live page, and checked by the language model for whether it supports its value" +
            (s.Unsupported.Count > 0 ? $" ({Count(s.Unsupported.Count, "value")} left out as not supported)" : "") + ". " +
            "For each research question, the findings were grouped in code by attribute and value, the map's values serving as the first codes; the language model then named the themes, placed the values in them, and named the tensions between sources within a theme. " +
            (placements.Count > 0
                ? $"A second, independent placement of the values in the other order agreed with the first with Cohen's κ = {string.Join(", ", placements.Select(a => $"{a.Kappa!.Value.ToString("0.00", CultureInfo.InvariantCulture)} for {a.Question}"))}; it did not change the themes. "
                : "") +
            $"The review produced {Count(shown.Count, "theme")}. What each theme rests on (how many sources, their outlet tiers and quality points, grey or formal literature) was counted in code, never by the model." +
            (notCoded > 0 ? $" {Count(notCoded, "finding")} had the map's \"Other\" value; they are listed under their question as not coded by the map, not themed." : "") +
            (s.Unassigned.Count > 0 ? $" {Count(s.Unassigned.Count, "value")} fit no theme; they are listed with the reason." : "") +
            (input.Ledger.HasFormalPool ? MultivocalFormalText.SynthesisText(input) : "")));
        if (s.Notes.Count > 0)
        {
            section.Blocks.Add(ReportBlock.Paragraph("Notes recorded by the synthesis:"));
            section.Blocks.Add(ReportBlock.List(s.Notes));
        }
        return section;
    }
}
