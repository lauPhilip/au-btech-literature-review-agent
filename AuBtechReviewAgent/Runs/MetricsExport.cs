using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuBtechReviewAgent;

/// <summary>
/// The run metrics as data for a paper (issue #416): /metrics/runs.csv and /metrics/runs.json, one row per finished
/// run, and /metrics/stages.csv, the effort of each run by stage and by model. The columns are fixed and in a fixed
/// order, so a script that reads them keeps working; docs/metrics-dictionary.md says what each one is, and a test keeps
/// the two in step. Privacy as on the Metrics page: only the first eight characters of a run id (the whole id opens the
/// run's archive), no research question or its hash, no paper text, no reviewer or user data.
/// </summary>
public static class MetricsExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One column: its name in the header and how a run's value is read (null for an empty cell).</summary>
    public sealed record Column(string Name, Func<RunMetrics, object?> Value);

    private static Column C(string name, Func<RunMetrics, object?> value) => new(name, value);

    /// <summary>Every column of runs.csv and runs.json, in order.</summary>
    public static IReadOnlyList<Column> Columns(MetricsPrices? prices = null) => RunColumns
        .Concat(new[] { C("estimated_cost", r => (prices ?? MetricsPrices.None).Cost(r)), C("cost_currency", r => (prices ?? MetricsPrices.None).Cost(r) == null ? null : prices!.Currency) })
        .Concat(PoolColumns("grey", r => r.GreyPool)).Concat(PoolColumns("formal", r => r.FormalPool)).ToList();

    private static readonly Column[] RunColumns =
    {
        C("run", r => MetricsOverview.ShortId(r.RunId)), C("review_type", r => r.Type), C("completed_utc", r => r.CompletedUtc),
        C("tool_version", r => r.BeforeVersioning ? null : ToolVersion.Number(r.AppVersion)), C("git_sha", r => ToolVersion.Commit(r.AppVersion)),
        C("schema_version", r => r.SchemaVersion), C("config_fingerprint", r => r.ConfigFingerprint), C("model", r => r.Model),
        C("settings", r => string.Join(";", r.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))),
        C("identified", r => r.Identified), C("duplicates_removed", r => r.DuplicatesRemoved), C("screened", r => r.Screened), C("included", r => r.Included),
        C("screening_kappa", r => r.ScreeningKappa), C("screening_kappa_basis", r => r.ScreeningKappa == null ? null : r.ScreeningKappaBasis),
        C("uncertain_decisions", r => r.UncertainDecisions), C("full_text_retrieved", r => r.FullTextRetrieved), C("full_text_share", r => r.FullTextShare),
        C("key_findings", r => r.KeyFindings), C("verified_finding_share", r => r.VerifiedFindingShare), C("extraction_errors", r => r.ExtractionErrors),
        C("values_rejected", r => r.ValuesRejected), C("mean_quality_points", r => r.MeanQualityPoints), C("max_quality_points", r => r.MaxQualityPoints),
        C("thematic_synthesis_used", r => r.ThematicSynthesisUsed), C("themes", r => r.Themes), C("codes", r => r.Codes),
        C("anchored_code_share", r => r.AnchoredCodeShare), C("studies_coded", r => r.StudiesCoded), C("studies_uncoded", r => r.StudiesUncoded),
        C("coding_kappa", r => r.CodingKappa), C("studies_cited", r => r.StudiesCited), C("coverage_share", r => r.CoverageShare),
        C("citations_initial", r => r.CitationsInitial.Total), C("initial_supported", r => r.CitationsInitial.Supported),
        C("initial_partly_supported", r => r.CitationsInitial.PartiallySupported), C("initial_not_supported", r => r.CitationsInitial.NotSupported),
        C("initial_insufficient_evidence", r => r.CitationsInitial.InsufficientEvidence), C("initial_unverifiable", r => r.CitationsInitial.Unverifiable),
        C("citations", r => r.CitationsFinal.Total), C("supported", r => r.CitationsFinal.Supported), C("partly_supported", r => r.CitationsFinal.PartiallySupported),
        C("not_supported", r => r.CitationsFinal.NotSupported), C("insufficient_evidence", r => r.CitationsFinal.InsufficientEvidence),
        C("unverifiable", r => r.CitationsFinal.Unverifiable), C("supported_share", r => r.CitationsFinal.SupportedShare),
        C("second_checked", r => r.SecondChecked), C("second_check_agreed", r => r.SecondCheckAgreed), C("upgraded_by_second_check", r => r.UpgradedBySecondCheck),
        C("repaired", r => r.Repaired), C("removed", r => r.Removed), C("outside_theme", r => r.OutsideTheme), C("invalid_markers_stripped", r => r.InvalidMarkersStripped),
        C("synthesis_words", r => r.SynthesisWords), C("discussion_words", r => r.DiscussionWords), C("sentences", r => r.Sentences),
        C("cited_sentence_share", r => r.CitedSentenceShare), C("citations_per_100_words", r => r.CitationsPer100Words),
        C("multi_paper_sentence_share", r => r.MultiPaperSentenceShare),
        C("duration_minutes", r => r.DurationMinutes), C("llm_minutes", r => r.LlmMinutes), C("llm_calls", r => r.LlmCalls), C("llm_retries", r => r.LlmRetries),
        C("llm_failures", r => r.LlmFailures), C("calls_without_usage", r => r.CallsWithoutUsage), C("tokens", r => r.Tokens), C("input_tokens", r => r.InputTokens),
        C("cached_input_tokens", r => r.CachedInputTokens), C("output_tokens", r => r.OutputTokens), C("reasoning_tokens", r => r.ReasoningTokens),
    };

    private static IEnumerable<Column> PoolColumns(string prefix, Func<RunMetrics, PoolMetrics?> pool)
    {
        Column P(string name, Func<PoolMetrics, object?> value) => C($"{prefix}_{name}", r => pool(r) is { } p ? value(p) : null);
        yield return P("identified", p => p.Identified);
        yield return P("duplicates_removed", p => p.DuplicatesRemoved);
        yield return P("no_address_removed", p => p.NoAddressRemoved);
        yield return P("duplicates_at_screening", p => p.DuplicatesAtScreening);
        yield return P("not_screened", p => p.NotScreened);
        yield return P("screened", p => p.Screened);
        yield return P("included", p => p.Included);
        yield return P("excluded", p => p.Excluded);
        yield return P("kappa", p => p.Kappa);
        yield return P("kappa_basis", p => p.Kappa == null ? null : p.KappaBasis);
        yield return P("screened_twice", p => p.ScreenedTwice);
        yield return P("audit_sample", p => p.AuditSample);
        yield return P("targeted_second", p => p.TargetedSecond);
        yield return P("targeted_agreed", p => p.TargetedAgreed);
        yield return P("kept", p => p.Kept);
        yield return P("below_threshold", p => p.BelowThreshold);
        yield return P("not_assessed", p => p.NotAssessed);
        yield return P("extracted", p => p.Extracted);
        yield return P("not_extracted", p => p.NotExtracted);
        yield return P("abstract_only", p => p.AbstractOnly);
        if (prefix == "grey")
        {
            yield return P("table7_scored", p => p.Table7Scored);
            yield return P("mean_table7_points", p => p.MeanTable7Points);
        }
        else
        {
            yield return P("mmat_appraised", p => p.MmatAppraised);
            yield return P("mmat_empirical", p => p.MmatEmpirical);
        }
    }

    /// <summary>The columns of stages.csv: one row per run and stage, and per run and model.</summary>
    public static readonly string[] StageColumns =
    {
        "run", "review_type", "tool_version", "group", "name", "calls", "retries", "failures", "calls_without_usage",
        "input_tokens", "cached_input_tokens", "output_tokens", "reasoning_tokens", "tokens", "seconds",
    };

    /// <summary>runs.csv: a header and one line per run, oldest first; RFC 4180 quoting, invariant numbers.</summary>
    public static string RunsCsv(IEnumerable<RunMetrics> runs, MetricsPrices? prices = null)
    {
        var columns = Columns(prices);
        var sb = new StringBuilder();
        sb.Append(string.Join(",", columns.Select(c => c.Name))).Append("\r\n");
        foreach (var run in Ordered(runs))
            sb.Append(string.Join(",", columns.Select(c => Cell(c.Value(run))))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>runs.json: an array with one object per run, the CSV's columns with their types, plus the effort by stage and by model and the grey sources by search.</summary>
    public static string RunsJson(IEnumerable<RunMetrics> runs, MetricsPrices? prices = null)
    {
        var columns = Columns(prices);
        var array = new JsonArray();
        foreach (var run in Ordered(runs))
        {
            var row = new JsonObject();
            foreach (var c in columns) row[c.Name] = JsonValue(c.Value(run));
            row["cost_by_stage"] = JsonSerializer.SerializeToNode(run.CostByStage);
            row["cost_by_model"] = JsonSerializer.SerializeToNode(run.CostByModel);
            row["sources_by_search"] = run.IsMultivocal ? JsonSerializer.SerializeToNode(run.SourcesBySearch) : null;
            array.Add(row);
        }
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>stages.csv: the effort of every run by stage ("stage") and by model ("model"), in the long format a plot reads.</summary>
    public static string StagesCsv(IEnumerable<RunMetrics> runs)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", StageColumns)).Append("\r\n");
        foreach (var run in Ordered(runs))
        {
            var groups = run.CostByStage.Select(kv => ("stage", kv.Key, kv.Value))
                .Concat((run.CostByModel ?? new()).Select(kv => ("model", kv.Key, kv.Value)));
            foreach (var (group, name, s) in groups)
            {
                object?[] cells =
                {
                    MetricsOverview.ShortId(run.RunId), run.Type, run.BeforeVersioning ? null : ToolVersion.Number(run.AppVersion), group, name, s.Calls,
                    s.Retries, s.Failures, s.CallsWithoutUsage, s.InputTokens, s.CachedInputTokens, s.OutputTokens, s.ReasoningTokens, s.Tokens, s.Seconds,
                };
                sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<RunMetrics> Ordered(IEnumerable<RunMetrics> runs) =>
        runs.OrderBy(r => r.CompletedUtc, StringComparer.Ordinal).ThenBy(r => r.RunId, StringComparer.Ordinal);

    /// <summary>A CSV cell: numbers in invariant culture, booleans as true/false, text quoted when it needs to be; a leading =, +, - or @ in text is quoted with a ' so a spreadsheet does not run it.</summary>
    public static string Cell(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        double d => d.ToString("0.####", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => Quote(value.ToString() ?? ""),
    };

    private static string Quote(string text)
    {
        if (text.Length > 0 && "=+-@".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static JsonNode? JsonValue(object? value) => value switch
    {
        null => null,
        bool b => b,
        int i => i,
        long l => l,
        double d => d,
        _ => value.ToString(),
    };
}
