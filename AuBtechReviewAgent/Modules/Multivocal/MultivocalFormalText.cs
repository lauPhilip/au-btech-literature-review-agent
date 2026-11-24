using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// What the method of a review with both pools says about the formal pool (block I-6), written in code from the run's
/// files, never by the model: the academic searches, their screening, the MMAT appraisal on a full text or an abstract,
/// and how the pools meet in the map and the synthesis. The report and the paper add these to their own method only
/// when the run has a formal pool, so a grey literature review's method is unchanged.
/// </summary>
public static partial class MultivocalFormalText
{
    /// <summary>A search's name: an academic database by its catalogue name, a grey search by its label.</summary>
    public static string SearchName(string key) =>
        SourceCatalog.Find(key) is { } database ? database.DisplayName : MultivocalGuidelines.SearchLabel(key);

    /// <summary>
    /// What the method may say about the raw answers of these searches: that each was saved, or, when a search that
    /// answered left no raw file (Scopus and IEEE Xplore before #469), which databases' answers were not kept.
    /// </summary>
    public static string RawAnswersSentence(IEnumerable<GreySearchLog> searches)
    {
        var notKept = searches.Where(s => s.Error == null && s.Returned > 0 && string.IsNullOrEmpty(s.RawFile))
            .Select(s => SearchName(s.Source)).Distinct().ToList();
        if (notKept.Count == 0) return "Each raw answer was saved as received, with its SHA-256 fingerprint.";
        string names = notKept.Count == 1 ? notKept[0] : string.Join(", ", notKept.Take(notKept.Count - 1)) + " and " + notKept[^1];
        return $"Each raw answer was saved as received, with its SHA-256 fingerprint, except those of {names}, which were not kept.";
    }

    /// <summary>The formal pool's search (G7, G8): the databases, the strings, what they returned, and what was set aside.</summary>
    public static IReadOnlyList<ReportBlock> SearchBlocks(MultivocalReportInput input)
    {
        var plan = input.Planned.Plan;
        var ledger = input.Ledger;
        var searches = ledger.Searches.Where(s => s.Formal).ToList();
        var counts = MultivocalPools.For(true, ledger, null, null, null);
        var ran = searches.Where(s => s.Error == null).Select(s => s.Source).Distinct().Select(SearchName).ToList();
        string strings = string.Join(", ", plan.EffectiveFormalSearchStrings.Select(t => $"\"{t}\""));
        var blocks = new List<ReportBlock>
        {
            ReportBlock.Paragraph(
                $"The formal pool was searched in {Count(ran.Count, "academic database")} ({(ran.Count == 0 ? "none answered" : string.Join(", ", ran))}), the bibliographic databases of the systematic review, " +
                $"with its own {Count(plan.EffectiveFormalSearchStrings.Count, "search string")} ({strings}) under the same stopping rule (G7, G8). " +
                $"The databases returned {Count(counts.Identified, "record")}; {Num(counts.DuplicatesRemoved)} {Was(counts.DuplicatesRemoved)} set aside as duplicates by identifier, DOI or title, as in the systematic review, " +
                $"and {Num(counts.NoAddressRemoved)} because {(counts.NoAddressRemoved == 1 ? "it" : "they")} had no web address to cite, leaving {Count(ledger.FormalPool.Count(), "academic source")}. " +
                RawAnswersSentence(searches) + " The two pools were kept and counted apart."),
            ReportBlock.Table(new[] { "Database", "Search string", "Returned", "New" },
                searches.Select(s => new[] { SearchName(s.Source), s.SearchString, s.Error == null ? Num(s.Returned) : $"failed: {s.Error}", Num(s.New) })),
        };
        blocks.AddRange(SeedBlocks(ledger));
        return blocks;
    }

    /// <summary>
    /// The formal pool's seed step, when it ran: one round of citation chaining from the most-cited included academic
    /// sources, in the words the systematic review uses (<see cref="MethodsSectionWriter.SeedChaining"/>), and the seeds.
    /// </summary>
    public static IReadOnlyList<ReportBlock> SeedBlocks(MultivocalLedger ledger)
    {
        var record = ledger.SeedChaining;
        if (record == null) return Array.Empty<ReportBlock>();
        int kept = ledger.Sources.Count(s => s.FoundBy.Any(f => f.StartsWith("seed chaining:", StringComparison.Ordinal)) && s.FoundBy.All(f => f.StartsWith("seed chaining:", StringComparison.Ordinal)));
        var blocks = new List<ReportBlock>
        {
            ReportBlock.Paragraph(
                "After screening, one round of citation chaining was run through OpenAlex from the most-cited included academic sources, in both directions: the works a source cites and the works citing it. " +
                MethodsSectionWriter.SeedChaining(record, "included academic source", record.Seeds.Count > 0 ? "The table below lists the seeds. " : "", failedChainedOrdinarily: false) +
                (record.Seeds.Count > 0
                    ? $"Of the records found, {Count(kept, "new academic source")} joined the formal pool after the same duplicate checks as the formal search, and {(kept == 1 ? "it was" : "they were")} screened in the same way, with an audit sample of their own. Each raw answer was saved as received, with its SHA-256 fingerprint."
                    : "")),
        };
        if (record.Seeds.Count > 0)
            blocks.Add(ReportBlock.Table(new[] { "Rank", "Seed", "Year", "Citations", "Per year", "Kept (cites / cited by)" },
                record.Seeds.OrderBy(x => x.Rank).Select(x => new[]
                {
                    Num(x.Rank), x.Title, Num(x.Year), Num(x.CitedByCount), x.CitationsPerYear.ToString("0.0", CultureInfo.InvariantCulture),
                    x.Error == null ? $"{Num(x.Backward)} / {Num(x.Forward)}" : "look-up failed",
                })));
        return blocks;
    }

    /// <summary>The formal pool's screening (G9, G10): the same criteria and steps, with the agreement for each pool and for both.</summary>
    public static string SelectionText(MultivocalReportInput input)
    {
        var s = input.Screening;
        var grey = MultivocalPools.For(false, input.Ledger, s, null, null);
        var formal = MultivocalPools.For(true, input.Ledger, s, null, null);
        return "Grey and academic sources were screened with the same criteria, steps and answer format; only the opening of the prompt said what kind of source it was (G10). " +
               $"Of the {Count(formal.Screened, "academic source")} screened, {Num(formal.Included)} {Was(formal.Included)} included and {Num(formal.Excluded)} excluded; of the {Count(grey.Screened, "grey source")}, {Num(grey.Included)} {Was(grey.Included)} included and {Num(grey.Excluded)} excluded. " +
               MultivocalScreeningText.PoolAgreement(s);
    }

    /// <summary>The formal pool's appraisal (G11 does not cover it): MMAT on the full text or the abstract, and no threshold.</summary>
    public static string QualityText(MultivocalReportInput input)
    {
        var appraised = input.Quality.Sources.Where(q => q.Formal).ToList();
        var withMmat = appraised.Where(q => q.Mmat != null).ToList();
        int onAbstract = withMmat.Count(q => q.TextBasis == MultivocalPages.AbstractBasis);
        int empirical = withMmat.Count(q => q.Mmat!.Appraisal.Count > 0);
        return $"The {Count(appraised.Count, "academic source")} included {Was(appraised.Count)} appraised as in the systematic review, with the Mixed Methods Appraisal Tool (MMAT, 2018), not with Table 7, " +
               "which is written for grey literature; every answer rests on a quote found in the source's kept text. " +
               $"That text was its open-access full text where one was found and otherwise its abstract: {Num(withMmat.Count - onAbstract)} {(withMmat.Count - onAbstract == 1 ? "rests" : "rest")} on a full text and {Num(onAbstract)} on the abstract alone. " +
               $"{Num(empirical)} {Was(empirical)} appraised as {(empirical == 1 ? "an empirical study" : "empirical studies")} and {Num(withMmat.Count - empirical)} {Was(withMmat.Count - empirical)} not empirical, so MMAT does not apply to {(withMmat.Count - empirical == 1 ? "it" : "them")}. " +
               "The quality threshold applies to grey sources only, so every appraised academic source was kept" +
               (appraised.Count > withMmat.Count ? $"; {Num(appraised.Count - withMmat.Count)} could not be appraised{NotAppraisedReasons(appraised.Where(q => q.Mmat == null))}." : ".");
    }

    /// <summary>
    /// Why sources could not be appraised, each reason once with how many it holds for (#467): a real run repeated one
    /// reason eight times. One reason for all reads ", all because ..."; several read " (5: ...; 3: ...)".
    /// </summary>
    public static string NotAppraisedReasons(IEnumerable<GreyQuality> notAppraised)
    {
        var reasons = notAppraised
            .Select(q => string.IsNullOrWhiteSpace(q.NotAssessed) ? "no reason recorded" : q.NotAssessed.Trim().TrimEnd('.'))
            .GroupBy(r => r, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (reasons.Count == 0) return "";
        if (reasons.Count == 1) return reasons[0].Count() == 1 ? $" ({reasons[0].Key})" : $", all for the same reason ({reasons[0].Key})";
        return $" ({string.Join("; ", reasons.Select(g => $"{Num(g.Count())}: {g.Key}"))})";
    }

    /// <summary>How the formal pool was extracted (G12).</summary>
    public static string ExtractionText(MultivocalReportInput input)
    {
        var formal = input.Extraction.Sources.Where(s => s.Formal && s.Error == null).ToList();
        int onAbstract = formal.Count(s => s.TextBasis == MultivocalPages.AbstractBasis);
        int selected = formal.Count(s => s.Passages != null);
        return $" Grey and academic sources were mapped and extracted together; for the {Count(formal.Count, "academic source")} extracted, each quote was found in its full text, or for {Num(onAbstract)} of them in its abstract." +
               (selected == 0 ? "" : $" For {Num(selected)} long full {(selected == 1 ? "text" : "texts")}, the model was given the first passages and the passages that share the most words with the map and the questions, not the whole text; each quote was found in those passages.");
    }

    /// <summary>What the synthesis says about the two pools (G13): what the themes rest on, and where the state of the art and of the practice disagree.</summary>
    public static string SynthesisText(MultivocalReportInput input)
    {
        var themes = MultivocalPaperParts.ShownThemes(input);
        int both = themes.Count(t => t.GreySources > 0 && t.FormalSources > 0);
        int formalOnly = themes.Count(t => t.GreySources == 0 && t.FormalSources > 0);
        int across = themes.Sum(t => t.Tensions.Count(x => x.Between == SynthesisTension.ArtAgainstPractice));
        return $" Each finding was labelled in code as grey or formal literature, so each theme says what it rests on: {Num(both)} of the {Count(themes.Count, "theme")} rest on both pools, {Num(formalOnly)} on formal literature only and {Num(themes.Count - both - formalOnly)} on grey literature only. " +
               $"A tension between an academic and a grey source was recorded as one between the state of the art and the state of the practice; there {(across == 1 ? "was" : "were")} {Num(across)}. " +
               "A theme's mean quality counts its grey sources only, since academic sources have no Table 7 points.";
    }

    /// <summary>
    /// What a theme rests on after its sources: their outlet tiers and the mean quality of the grey ones, and its label.
    /// A theme on formal literature only has neither (#467: a real run said "a mean quality of the grey sources of 0").
    /// </summary>
    public static string ThemeBasis(SynthesisTheme theme, int maxPoints)
    {
        if (theme.GreySources == 0 && theme.FormalSources > 0)
            return $"all academic, so without outlet tiers or quality points ({theme.EvidenceLabel})";
        return $"{Num(theme.FirstTier)} from 1st-tier, {Num(theme.SecondTier)} from 2nd-tier and {Num(theme.ThirdTier)} from 3rd-tier outlets, " +
               $"with a mean quality {(theme.FormalSources > 0 ? "of the grey sources " : "")}of {theme.MeanQuality.ToString("0.#", CultureInfo.InvariantCulture)} of {Num(maxPoints)} points ({theme.EvidenceLabel})";
    }

    /// <summary>
    /// The statement on the use of AI, the part that says what the model did, for a review with both pools (#362): both
    /// pools screened, two appraisals, and citations checked against a grey source's page or an academic source's text.
    /// </summary>
    public static string AiUseTasks(MultivocalReportInput input, string subject = "The model was") =>
        subject + " used for " + MultivocalScreeningText.AiUseTask(input.Screening, "the grey and the academic sources", "the same criteria") + ", scoring the grey sources' quality on the guidelines' checklist, " +
        "appraising the academic sources with the Mixed Methods Appraisal Tool, proposing the systematic map, extracting each source's values, checking that each value's quote supports it, " +
        "grouping the findings into themes, writing the theme sections, the discussion, the summary for practitioners and the title, abstract and introduction, " +
        "and checking each citation against the kept text of the source it cites: the kept page of a grey source, and the open-access full text or else the abstract of an academic one. " +
        "Every value, quality point, appraisal answer and citation verdict counts only with a quote found verbatim in that kept text; the method, the counts, the figures, the answers to questions about frequency or existence and the references were written in code. ";

    /// <summary>The sentence on the seed step for the statement on the use of AI; empty when no seed was chosen.</summary>
    public static string AiUseSeeds(MultivocalReportInput input) => input.Ledger.SeedChaining is { Seeds.Count: > 0 }
        ? "The seed studies for citation chaining in the formal pool were chosen in code from the citation counts in the databases' metadata, not by the model. "
        : "";

    /// <summary>The screenings' agreement for the statement on the use of AI: each pool and both, on the audit sample, as the method gives it.</summary>
    public static string AiUseKappa(MultivocalReportInput input) => MultivocalScreeningText.AiUseAgreement(input.Screening, bothPools: true);

    private static string Was(int n) => n == 1 ? "was" : "were";

    private static string Kappa(double? k) => k is double v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "not computed";
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Count(int n, string noun) => $"{Num(n)} {(n == 1 ? noun : noun + "s")}";
}
