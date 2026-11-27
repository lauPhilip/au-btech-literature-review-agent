using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// The self-check of a systematic review (<see cref="PaperSelfCheck"/>): the paper's parts from prisma-report.json, the
/// counts, themes and references from the ledger and the citation check from citation-audit.json. It runs once the
/// report files are written and saves self-check.json; a run made before it is checked when its report is opened.
/// </summary>
public static class SystematicSelfCheck
{
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    /// <summary>What citation-audit.json says about the check: after and before repair, the steps of the check, the removed citations and the studies not cited.</summary>
    public sealed record Audit(CitationSupportSummary? Final, CitationSupportSummary? Initial, int Removed, IReadOnlyList<UncodedStudy> NotCited, CitationCheckSteps? Steps = null, PaperMapLinks? MapLinks = null);

    /// <summary>The paper, counts and checks of a finished systematic review, in the self-check's shape.</summary>
    public static PaperCheckInput Input(PrismaReport report, ReviewState state, Audit audit)
    {
        var book = state.ThematicSynthesis;
        var records = state.SynthesizedRecords.Select((r, i) => (Number: r.ReferenceNumber > 0 ? r.ReferenceNumber : i + 1, Record: r)).OrderBy(r => r.Number).ToList();
        var parts = new List<PaperPart>
        {
            new("abstractItem", "Abstract", PaperPartKind.Abstract, report.AbstractItem),
            new("rationaleItem", "Rationale", PaperPartKind.Introduction, report.RationaleItem),
            new("objectivesItem", "Objectives", PaperPartKind.Introduction, report.ObjectivesItem),
            new("synthesisResultsItem", "Results overview", PaperPartKind.Overview, SpecText(report.SynthesisResultsItem)),
        };
        parts.AddRange(report.SynthesisSections.Select(x => new PaperPart(x.Field, "Theme: " + x.Heading, PaperPartKind.Theme, x.Text)));
        parts.Add(new("discussionItem", "Discussion", PaperPartKind.Discussion, report.DiscussionItem));
        parts.AddRange(report.ResearchAnswers.Select(a => new PaperPart($"researchAnswer{a.Number}", $"Answer to RQ{a.Number}", PaperPartKind.Answer, a.Answer)));
        parts.Add(new("evidenceLimitationsItem", "Limitations of the evidence", PaperPartKind.Limitations, report.EvidenceLimitationsItem));
        parts.Add(new("implicationsItem", "Implications", PaperPartKind.Implications, report.ImplicationsItem));
        parts.Add(new("futureResearchItem", "Future research", PaperPartKind.FutureResearch, report.FutureResearchItem));
        parts.Add(new("conclusionItem", "Conclusion", PaperPartKind.Conclusion, report.ConclusionItem));
        if (MapText(report.Artifact) is { Length: > 0 } map)
            parts.Add(new("artifact", ArtifactPartTitle(report.Artifact!.Kind), PaperPartKind.ConceptMap, map));
        parts.Add(new("reviewLimitationsItem", "Limitations of the review", PaperPartKind.CodeWritten, report.ReviewLimitationsItem));
        parts.Add(new("coverage", "Coverage note", PaperPartKind.CodeWritten, report.CoverageSummary));
        parts.Add(new("citationCheck", "Citation check note", PaperPartKind.CodeWritten, report.CitationCheckSummary));

        var themesById = book?.Themes.ToDictionary(t => t.Id) ?? new();
        var questions = state.ResearchQuestions ?? new List<string>();
        return new PaperCheckInput
        {
            Parts = parts,
            Facts = Facts(state),
            MapLinks = audit.MapLinks,
            FactsText = SystematicEvidence.BaseFacts(state.Extractions ?? new List<StudyExtraction>()),
            Themes = report.SynthesisSections.Select(x => themesById.TryGetValue(x.ThemeId, out var t)
                ? new PaperTheme(x.Heading, t.Studies, t.SingleStudyReason) : new PaperTheme(x.Heading, ThematicSynthesis.CitedIn(x.Text).ToList())).ToList(),
            Questions = questions.Select((q, i) => new PaperQuestion($"RQ{i + 1}", q, report.ResearchAnswers.FirstOrDefault(a => a.Number == i + 1)?.Answer ?? "")).ToList(),
            References = records.Select(r => new PaperReference(r.Number, r.Record.ApaCitation, r.Record.Year, r.Record.VenueName, r.Record.Doi, r.Record.Title,
                r.Record.Details?.EnrichedFrom?.Count ?? 0)).ToList(),
            Included = records.Select(r => r.Number).ToList(),
            NotCitedReasons = NotCitedReasons(audit, book),
            CitationsFinal = audit.Final,
            CitationsInitial = audit.Initial,
            CitationsRemoved = audit.Removed,
            CitationSteps = audit.Steps,
            CitationsShown = new CitationSupportSummary
            {
                Checked = state.Stats.CitationsChecked, Supported = state.Stats.CitationsSupported, PartiallySupported = state.Stats.CitationsPartiallySupported,
                NotSupported = state.Stats.CitationsNotSupported, Removed = state.Stats.CitationsRemoved,
            },
        };
    }

    /// <summary>
    /// The text the artifact shows, one line per box and link label of a diagram, per row of a table or per item of a list,
    /// so check 5 counts a figure it repeats (issues #695, #702); empty for an artifact that failed or shows no text.
    /// </summary>
    public static string MapText(ReviewArtifact? artifact)
    {
        if (artifact is not { Error: null }) return "";
        var lines = artifact.IsDiagram ? artifact.Nodes.Select(n => n.Label).Concat(artifact.Edges.Select(e => e.Label))
            : artifact.Kind == ArtifactKinds.Table ? artifact.Rows.Select(r => string.Join("; ", r.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().TrimEnd('.'))))
            : artifact.Kind == ArtifactKinds.Bullets ? artifact.Bullets
            : Enumerable.Empty<string>();
        return string.Join("\n", lines.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().TrimEnd('.') + "."));
    }

    private static string ArtifactPartTitle(string kind) => kind switch
    {
        ArtifactKinds.Flowchart => "Flowchart",
        ArtifactKinds.Table => "Comparison table",
        ArtifactKinds.Bullets => "List",
        _ => "Concept map",
    };

    /// <summary>Why each study is not cited: the coverage's reason, or else that the codebook placed it in no theme, and why.</summary>
    private static Dictionary<int, string> NotCitedReasons(Audit audit, ThematicCodebook? book)
    {
        var reasons = new Dictionary<int, string>();
        foreach (var n in audit.NotCited.Concat(book?.NotCited ?? new()).Where(n => !string.IsNullOrWhiteSpace(n.Reason))) reasons.TryAdd(n.Reference, n.Reason);
        foreach (var u in (book?.Uncoded ?? new()).Where(u => !string.IsNullOrWhiteSpace(u.Reason))) reasons.TryAdd(u.Reference, "placed in no theme: " + u.Reason);
        return reasons;
    }

    // The results' opening text without the diagram blocks the page leaves out.
    private static string SpecText(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\[(MERMAID|TIKZ)_START\].*?\[\1_END\]", "", System.Text.RegularExpressions.RegexOptions.Singleline).Trim();

    /// <summary>The run's counts, each with the words a sentence names it by.</summary>
    public static List<PaperFact> Facts(ReviewState state)
    {
        var s = state.Stats;
        var book = state.ThematicSynthesis;
        var facts = new List<PaperFact>
        {
            new("records identified", s.TotalIdentified, "identif", "found", "retriev", "returned", "yield", "located"),
            new("duplicates removed", s.DuplicatesRemoved, "duplicat"),
            new("records outside the year range", s.OutsideDateRange, "outside", "year range"),
            new("records over the per-source cap", s.CappedBeyondMaxResults, @"cap\b", "per-source"),
            new("records screened", s.Screened, "screen"),
            // "We could not screen 1 record" gives this count, not the screened one (issue #673); the methods say "could not be screened".
            new("records that could not be screened", s.ScreeningErrors, "could not (?:be )?screen", "not screened", "unscreened"),
            new("studies included", s.Included, "includ", "retain"),
            new("records excluded", s.Excluded, "exclud"),
            new("full texts read", s.FullTextRetrieved, "full[- ]?texts?"),
            new("studies read from the abstract", Math.Max(0, s.Included - s.FullTextRetrieved), "abstracts?"),
            new("research questions", (state.ResearchQuestions ?? new()).Count, "questions?"),
            new("citations checked", s.CitationsChecked, "citations?"),
        };
        if (book != null)
        {
            facts.Add(new("themes", book.Themes.Count, "themes?"));
            facts.Add(new("codes", book.Codes.Count, "codes?"));
            facts.Add(new("studies placed in no theme", book.Uncoded.Count, "no theme", "placed", "uncoded"));
        }
        foreach (var venue in state.SynthesizedRecords.GroupBy(r => r.VenueType))
        {
            string word = venue.Key switch { "Journals" => "journal", "Conferences" => "conference", "Preprints" => "preprint", "Proceedings" => "proceeding", _ => "" };
            if (word.Length > 0) facts.Add(new($"{word} papers", venue.Count(), word));
        }
        return facts;
    }

    /// <summary>Reads what the self-check needs from citation-audit.json; an empty audit when the file is missing or unreadable.</summary>
    public static Audit ReadAudit(string folder) => ReadCitationAudit(folder) with { MapLinks = ReadMapLinks(folder) };

    /// <summary>The link check of the diagram from artifact-links.json, which lists every link the model drew, before the cap; null when the run has none.</summary>
    public static PaperMapLinks? ReadMapLinks(string folder)
    {
        string path = Path.Join(folder, ArtifactLinks.FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var links = JsonSerializer.Deserialize<List<ArtifactLinkCheck>>(File.ReadAllText(path), Read);
            return links is { Count: > 0 } ? new PaperMapLinks(links.Count, links.Count(l => l.Check == ArtifactLinks.Reading), links.Count(l => l.Dropped)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static Audit ReadCitationAudit(string folder)
    {
        string path = Path.Join(folder, "citation-audit.json");
        try
        {
            if (!File.Exists(path)) return new(null, null, 0, Array.Empty<UncodedStudy>());
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            T? Get<T>(string name) where T : class => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.Deserialize<T>(Read) : null;
            int removed = root.TryGetProperty("RemovedCitations", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
            return new(Get<CitationSupportSummary>("SupportSummary"), Get<CitationSupportSummary>("InitialSupportSummary"), removed, Get<List<UncodedStudy>>("NotCited") ?? new(), Get<CitationCheckSteps>("CheckSteps"));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new(null, null, 0, Array.Empty<UncodedStudy>());
        }
    }

    /// <summary>Checks the finished paper in <paramref name="folder"/>; null when the run has no report.</summary>
    public static PaperSelfCheckFile? Check(string folder, ReviewState state)
    {
        string reportPath = Path.Join(folder, "prisma-report.json");
        if (!File.Exists(reportPath)) return null;
        var report = JsonSerializer.Deserialize<PrismaReport>(File.ReadAllText(reportPath), Read);
        if (report == null || string.IsNullOrWhiteSpace(report.AbstractItem) || report.AbstractItem == "Pending...") return null;
        return PaperSelfCheck.Run(Input(report, state, ReadAudit(folder)));
    }

    /// <summary>Checks the paper, writes self-check.json and puts the counts in the ledger's figures; null when there is no report.</summary>
    public static PaperSelfCheckFile? WriteFile(string folder, ReviewState state)
    {
        var file = Check(folder, state);
        if (file == null) return null;
        File.WriteAllText(Path.Join(folder, PaperSelfCheckFile.FileName), JsonSerializer.Serialize(file, Write));
        state.Stats.SelfCheckPassed = file.Passed;
        state.Stats.SelfCheckNotes = file.Notes;
        state.Stats.SelfCheckFaults = file.Faults;
        return file;
    }

    /// <summary><see cref="WriteFile"/> for the end of a run: a failure is logged and never fails the run, which is already written.</summary>
    public static void TryWriteFile(string folder, ReviewState state, Microsoft.Extensions.Logging.ILogger log)
    {
        try { WriteFile(folder, state); }
        catch (Exception ex)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(log, "The self-check was not written: {Message}", PrismaReviewEngine.SanitizeLogMessage(ex.Message));
        }
    }

    /// <summary>The self-check on file, or for a run made before it a fresh one from its files (not saved); null without a report.</summary>
    public static PaperSelfCheckFile? Load(string folder)
    {
        string path = Path.Join(folder, PaperSelfCheckFile.FileName);
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<PaperSelfCheckFile>(File.ReadAllText(path), Read);
            var state = GallerySnapshot.LoadLedger(folder);
            return state == null ? null : Check(folder, state);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
