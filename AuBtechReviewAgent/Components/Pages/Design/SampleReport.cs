using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// A made-up finished systematic review, written to a run folder so the development page /design/report can show
/// the report page (Components/Pages/SpecMatrix.razor) without a real run, and CI's accessibility job can check and
/// photograph it. Every value is sample text. The citation verdicts are attached sentence by sentence exactly as the
/// citation check would split the text, so every [n] on the page finds its verdict.
/// </summary>
public static class SampleReport
{
    /// <summary>The run id of the sample; its folder is rewritten each time the page opens.</summary>
    public static readonly Guid RunId = Guid.Parse("00000000-0000-4000-8000-000000000008");

    private static readonly (string Title, string Authors, int Year, string Venue, string Type, string Quartile, string Category)[] Studies =
    {
        ("Guardrails for tool-using language agents", "Hansen, M., & Okafor, L.", 2025, "Journal of Systems and Software", "Journals", "Q1", "OpenAlex"),
        ("Recovering from failed actions in agent workflows", "Ito, R., Berg, S., & Laursen, P.", 2024, "Proceedings of ICSE", "Conferences", "N/A", "OpenAlex"),
        ("A taxonomy of faults in multi-agent frameworks", "Moreau, C.", 2025, "Empirical Software Engineering", "Journals", "Q1", "Crossref"),
        ("Evaluating agent safety with sandboxed benchmarks", "Patel, D., & Nygaard, E.", 2026, "arXiv", "Preprints", "N/A", "Arxiv"),
        ("Human approval gates in autonomous coding agents", "Svensson, K., & Ali, H.", 2025, "Information and Software Technology", "Journals", "Q2", "Crossref"),
        ("Checkpointing and rollback for LLM agents", "Lund, A.", 2024, "IEEE Software", "Journals", "Q2", "OpenAlex"),
    };

    private const string Overview =
        "Six studies describe how agentic frameworks guard against faults and how those guards are tested [1, 3]. " +
        "Most mechanisms act before a tool call, a smaller group acts after a failure [2, 6]. " +
        "Evaluation relies on sandboxed benchmarks more than on field data [4].";

    private static readonly (string Heading, string Text)[] Themes =
    {
        ("Guardrails before an action",
            "Policy checks that run before a tool call are the most common mechanism [1]. " +
            "They block actions outside an allow list and ask for approval when a call is risky [1, 5]. " +
            "One study reports that approval gates removed every destructive file operation in its trials [5].\n\n" +
            "The checks are mostly written by hand, and two studies note that they drift from the agent's real tool set [3]."),
        ("Recovery after a failure",
            "When an action fails, frameworks retry, roll back or hand the task to a person [2]. " +
            "Checkpoints let an agent return to a known state before it tries again [6]. " +
            "Rollback is rarely tested against side effects outside the agent's own workspace [6, 2]."),
        ("How safety is evaluated",
            "Sandboxed benchmarks with scripted faults are the main way mechanisms are evaluated [4]. " +
            "Few studies measure how often a guard stops a harmless action by mistake [4, 3]."),
    };

    private const string Discussion =
        "The evidence favours prevention over recovery, which leaves agents exposed once a guard is passed [1, 6]. " +
        "Field studies of deployed agents are missing, so the benchmark results may not hold in practice [4].";

    private const string Conclusion =
        "Agentic frameworks rely on checks before an action and on checkpoints after a failure [1, 6]. " +
        "Their evaluation should move from scripted benchmarks to deployed systems [4].";

    /// <summary>Writes the sample's ledger, report, citation audit and protocol to <paramref name="folder"/>.</summary>
    public static void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = Report();
        var checks = Checks(report);
        var state = State(checks);

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Join(folder, "transparent-process.json"), JsonSerializer.Serialize(state, options));
        File.WriteAllText(Path.Join(folder, "prisma-report.json"), JsonSerializer.Serialize(report, options));
        File.WriteAllText(Path.Join(folder, "citation-audit.json"), JsonSerializer.Serialize(new { SupportChecks = checks }, options));
        File.WriteAllText(Path.Join(folder, "protocol.md"), "# Protocol (sample)\n\nA made-up protocol for the design preview.\n");
    }

    public static PrismaReport Report()
    {
        var report = new PrismaReport
        {
            GeneratedAt = "2026-11-21 10:00 UTC",
            ProtocolHash = "sample00",
            TitleItem = "Safety and fault tolerance in agentic AI frameworks: a systematic review",
            AbstractItem = "Background: agentic AI frameworks let language models call tools, which makes faults costly. " +
                "Methods: a PRISMA 2020 review of four open databases, with every record screened twice. " +
                "Results: six studies were included; they favour checks before an action over recovery after a failure. " +
                "Conclusion: evaluation should move from sandboxed benchmarks to deployed systems. This is sample text.",
            RationaleItem = "Agents that act on their own can delete files or send messages that cannot be taken back [1]. " +
                "Reviews of their safety mechanisms are still few [3].",
            ObjectivesItem = "To map the safety and fault-tolerance mechanisms of agentic AI frameworks and how they are evaluated.",
            EligibilityItem = "Peer-reviewed studies and preprints from 2020 to 2026 that describe or evaluate a safety or recovery mechanism of an agent framework.",
            SourcesItem = "OpenAlex, Crossref, arXiv and Semantic Scholar, searched on 21 November 2026.",
            SearchStrategyItem = "One search string and two variants, run against each source with a cap of 100 records per source.",
            SelectionProcessItem = "Every title and abstract was screened twice by independent prompts; disagreements were included and flagged.",
            BiasAssessmentItem = "Data were extracted with a verbatim quote for every value, and empirical studies were appraised with MMAT 2018.",
            SynthesisMethodsItem = "Findings were coded into themes, and each theme was written as one cited subsection.",
            SynthesisResultsItem = Overview,
            DiscussionItem = Discussion,
            ReviewLimitationsItem = "The review searched open databases only, and the screening was done by a language model.",
            ConclusionItem = Conclusion,
            SupportItem = "No funding. Department of Business Development and Technology, Aarhus University.",
            AvailabilityItem = "The run archive holds every file of the review, fingerprinted with SHA-256.",
            ProtocolItem = "The protocol was written and fingerprinted before the search.",
            CoverageSummary = "The synthesis cites all six included studies.",
        };
        for (int k = 0; k < Themes.Length; k++)
            report.SynthesisSections.Add(new SynthesisSection { Field = $"synthesis-{k + 1}", ThemeId = $"T{k + 1}", Heading = Themes[k].Heading, Text = Themes[k].Text });
        return report;
    }

    /// <summary>One verdict for every [n] in every cited text, split into sentences the way the citation check splits them.</summary>
    public static List<CitationSupportResult> Checks(PrismaReport report)
    {
        var fields = new List<(string Field, string Text)> { ("rationaleItem", report.RationaleItem), ("synthesisResultsItem", report.SynthesisResultsItem) };
        fields.AddRange(report.SynthesisSections.Select(s => (s.Field, s.Text)));
        fields.Add(("discussionItem", report.DiscussionItem));
        fields.Add(("conclusionItem", report.ConclusionItem));

        // Mostly supported, with one of each other verdict, so every colour shows on the page.
        string[] pattern =
        {
            CitationSupportChecker.Supported, CitationSupportChecker.Supported, CitationSupportChecker.Partial,
            CitationSupportChecker.Supported, CitationSupportChecker.Supported, CitationSupportChecker.NotSupported,
            CitationSupportChecker.Supported, CitationSupportChecker.InsufficientEvidence, CitationSupportChecker.Supported,
            CitationSupportChecker.Partial, CitationSupportChecker.Supported, CitationSupportChecker.Unverifiable,
        };
        var checks = new List<CitationSupportResult>();
        foreach (var (field, text) in fields)
        {
            var sentences = CitationSupportChecker.SplitSentences(text);
            for (int i = 0; i < sentences.Count; i++)
            {
                foreach (int reference in CitationSupportChecker.ReferencesIn(sentences[i]))
                {
                    string verdict = pattern[checks.Count % pattern.Length];
                    bool abstractOnly = verdict == CitationSupportChecker.InsufficientEvidence || reference == 4;
                    checks.Add(new CitationSupportResult
                    {
                        Field = field,
                        SentenceIndex = i,
                        Sentence = sentences[i],
                        Reference = reference,
                        Verdict = verdict,
                        FirstVerdict = verdict,
                        EvidenceBasis = abstractOnly ? "abstract only" : "full text",
                        EvidenceLocation = abstractOnly ? null : $"page {2 + reference}",
                        Quote = verdict == CitationSupportChecker.Unverifiable ? null : $"Sample passage from study {reference} that the check compared with the sentence.",
                        QuoteVerified = verdict != CitationSupportChecker.Unverifiable,
                        Reason = verdict switch
                        {
                            CitationSupportChecker.Supported => "The passage states the same finding.",
                            CitationSupportChecker.Partial => "The passage supports part of the sentence, not all of it.",
                            CitationSupportChecker.NotSupported => "The passage does not say this; the claim stays in the report, marked.",
                            CitationSupportChecker.InsufficientEvidence => "Only the abstract could be read, and it does not mention the claim.",
                            _ => "The paper's text could not be read.",
                        },
                    });
                }
            }
        }
        return checks;
    }

    private static ReviewState State(List<CitationSupportResult> checks)
    {
        var counts = VerdictTally.From(checks);
        var state = new ReviewState
        {
            ReviewObjective = "Which safety and fault-tolerance mechanisms do agentic AI frameworks use, and how are they evaluated?",
            SearchQuery = "agentic AI framework safety fault tolerance",
            PeerReviewOnlyToggle = false,
            CompletedUtc = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc),
            ResearchQuestions = new() { "Which safety mechanisms do agent frameworks use?", "How are they evaluated?" },
            RunSettings = new RunSettingsRecord { Model = "sample-model", AppVersion = "0.4.0+sample00" },
            Stats = new ReviewStats
            {
                ProcessingStage = PrismaReviewEngine.StageComplete,
                TotalIdentified = 412, DuplicatesRemoved = 96, OutsideDateRange = 18, CappedBeyondMaxResults = 0,
                Screened = 298, Excluded = 292, Included = 6, FullTextRetrieved = 5,
                DualScreened = 298, ScreeningDisagreements = 11, ScreeningKappa = 0.81,
                ThemesIdentified = Themes.Length, StudiesCitedInSynthesis = 6,
                CitationsChecked = counts.Total, CitationsSupported = counts.Supported, CitationsPartiallySupported = counts.Partly,
                CitationsNotSupported = counts.NotSupported, CitationsInsufficientEvidence = counts.AbstractOnly, CitationsUnverifiable = counts.Unverifiable,
            },
        };
        foreach (var (source, found) in new[] { ("OpenAlex", 180), ("Crossref", 120), ("arXiv", 70), ("Semantic Scholar", 42) })
            state.SearchLogs.Add(new PlatformSearchLog { SourceName = source, PapersFound = found, Status = "OK" });

        for (int i = 0; i < Studies.Length; i++)
        {
            var s = Studies[i];
            state.SynthesizedRecords.Add(new IncludedPaperMetricRow
            {
                ReferenceNumber = i + 1,
                PaperId = $"sample-{i + 1}",
                Title = s.Title,
                Year = s.Year,
                VenueType = s.Type,
                VenueName = s.Venue,
                Quartile = s.Quartile,
                Category = s.Category,
                Summary = "Sample summary of what the study did and found.",
                InclusionRationale = "Describes or evaluates a safety mechanism of an agent framework.",
                ApaCitation = $"{s.Authors} ({s.Year}). {s.Title}. *{s.Venue}*.",
                Doi = $"10.0000/sample.{i + 1}",
            });
            state.Extractions.Add(new StudyExtraction
            {
                ReferenceNumber = i + 1,
                PaperId = $"sample-{i + 1}",
                Title = s.Title,
                EvidenceBasis = i == 3 ? "abstract only" : "full text",
                StudyType = i == 2 ? "Taxonomy" : "Experiment",
                Method = new EvidencedValue { Value = "Controlled experiment", Quote = "We ran a controlled experiment.", QuoteVerified = true },
                Sample = new EvidencedValue { Value = $"{20 + 5 * i} agent tasks", Quote = "tasks", QuoteVerified = i != 4 },
                KeyFindings = new() { new EvidencedValue { Value = "Checks before an action prevented most faults.", Quote = "prevented most faults", QuoteVerified = true } },
                AppraisalCategory = i == 2 ? "not_empirical" : "quantitative_nonrandomized",
                Appraisal = i == 2 ? new() : Enumerable.Range(1, 5).Select(q => new AppraisalAnswer
                {
                    Id = $"3.{q}",
                    Criterion = $"Sample MMAT question {q}.",
                    Answer = (q + i) % 4 == 0 ? "cant_tell" : (q + i) % 5 == 0 ? "no" : "yes",
                }).ToList(),
            });
        }
        return state;
    }
}
