using System.Text.Json;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The run metrics as files (issue #416): a fixed header, one row per run, CSV that a spreadsheet reads safely, the
/// optional price table off by default, the same columns in the JSON and in the data dictionary, and nothing private.
/// </summary>
public class MetricsExportTests
{
    // The header of runs.csv, pinned on purpose: a script that reads the file must not break. A new column goes at the
    // end of its group with a line in docs/metrics-dictionary.md, and this list changes with it.
    private static readonly string RunsHeader =
        "run,review_type,completed_utc,tool_version,git_sha,schema_version,config_fingerprint,model,settings," +
        "identified,duplicates_removed,screened,included,screening_kappa,screening_kappa_basis,uncertain_decisions,full_text_retrieved,full_text_share," +
        "key_findings,verified_finding_share,extraction_errors,values_rejected,mean_quality_points,max_quality_points," +
        "thematic_synthesis_used,themes,codes,anchored_code_share,studies_coded,studies_uncoded,coding_kappa,studies_cited,coverage_share," +
        "citations_initial,initial_supported,initial_partly_supported,initial_not_supported,initial_insufficient_evidence,initial_unverifiable," +
        "citations,supported,partly_supported,not_supported,insufficient_evidence,unverifiable,supported_share," +
        "second_checked,second_check_agreed,upgraded_by_second_check,repaired,removed,outside_theme,invalid_markers_stripped," +
        "synthesis_words,discussion_words,sentences,cited_sentence_share,citations_per_100_words,multi_paper_sentence_share," +
        "duration_minutes,llm_minutes,llm_calls,llm_retries,llm_failures,calls_without_usage,tokens,input_tokens,cached_input_tokens,output_tokens,reasoning_tokens," +
        "estimated_cost,cost_currency," + Pool("grey", "table7_scored,mean_table7_points") + "," + Pool("formal", "mmat_appraised,mmat_empirical");

    private static string Pool(string prefix, string own) => string.Join(",",
        "identified,duplicates_removed,no_address_removed,duplicates_at_screening,not_screened,screened,included,excluded,kappa,kappa_basis,screened_twice,audit_sample,targeted_second,targeted_agreed,kept,below_threshold,not_assessed,extracted,not_extracted,abstract_only"
            .Split(',').Concat(own.Split(',')).Select(c => $"{prefix}_{c}"));

    private const string StagesHeader = "run,review_type,tool_version,group,name,calls,retries,failures,calls_without_usage,input_tokens,cached_input_tokens,output_tokens,reasoning_tokens,tokens,seconds";

    private static RunMetrics Systematic() => new()
    {
        RunId = "0123456789abcdef0123456789abcdef", CompletedUtc = "2026-11-20T10:00:00Z", AppVersion = "0.5.0+40b00a0c59f6b49e121c30d1d6d98ac8f3d3c961",
        Model = "mistral-large-latest", QueryHash = "e026e4ed8f3b", ReviewType = "systematic", ScreeningKappa = 0.81, ScreeningKappaBasis = "all records",
        Settings = new() { ["DualScreening"] = "True", ["Note"] = "=cmd, \"quoted\"" },
        CitationsFinal = new VerdictCounts { Total = 4, Supported = 3, NotSupported = 1 },
        LlmCalls = 2, Tokens = 300, InputTokens = 200, CachedInputTokens = 100, OutputTokens = 100, ReasoningTokens = 80, LlmMinutes = 1.5,
        CostByStage = new() { ["screening"] = new StageCost { Calls = 2, Seconds = 90, Tokens = 300, InputTokens = 200, OutputTokens = 100, ReasoningTokens = 80, Retries = 1, Failures = 0 } },
        CostByModel = new() { ["mistral-large-latest"] = new StageCost { Calls = 2, Tokens = 300, InputTokens = 200, CachedInputTokens = 100, OutputTokens = 100 } },
    };

    private static RunMetrics Multivocal() => new()
    {
        RunId = "fedcba9876543210fedcba9876543210", CompletedUtc = "2026-11-21T10:00:00Z", AppVersion = "1.0.0", SchemaVersion = 1, ReviewType = "multivocal",
        GreyPool = new PoolMetrics { Identified = 12, Kappa = 0.6, KappaBasis = "audit sample", AuditSample = 10, Table7Scored = 5, MeanTable7Points = 11.5 },
        SourcesBySearch = new() { ["stackexchange"] = 12 },
    };

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void TheHeaderOfRunsCsvStaysTheSame()
    {
        Assert.Equal(RunsHeader, Lines(MetricsExport.RunsCsv(Array.Empty<RunMetrics>()))[0]);
        Assert.Equal(StagesHeader, Lines(MetricsExport.StagesCsv(Array.Empty<RunMetrics>()))[0]);
        Assert.Equal(RunsHeader.Split(',').Length, RunsHeader.Split(',').Distinct().Count());
    }

    [Fact]
    public void EachRunIsOneRowOldestFirstWithItsVersionAndCommit()
    {
        var rows = Lines(MetricsExport.RunsCsv(new[] { Multivocal(), Systematic() }));
        var header = rows[0].Split(',');
        Assert.Equal(3, rows.Length);
        string Value(int row, string column) => Cells(rows[row])[Array.IndexOf(header, column)];

        Assert.Equal(("01234567", "systematic", "0.5.0", "40b00a0c59f6b49e121c30d1d6d98ac8f3d3c961"), (Value(1, "run"), Value(1, "review_type"), Value(1, "tool_version"), Value(1, "git_sha")));
        Assert.Equal(("0.81", "all records", "0.75", "80"), (Value(1, "screening_kappa"), Value(1, "screening_kappa_basis"), Value(1, "supported_share"), Value(1, "reasoning_tokens")));
        Assert.Equal("", Value(1, "grey_identified")); // a systematic run has no pools
        Assert.Equal(("multivocal", "", "12", "audit sample", "11.5"), (Value(2, "review_type"), Value(2, "tool_version"), Value(2, "grey_identified"), Value(2, "grey_kappa_basis"), Value(2, "grey_mean_table7_points")));
        Assert.Equal("", Value(2, "formal_identified"));
        Assert.Equal("", Value(2, "input_tokens")); // not counted then: empty, not 0
        Assert.All(rows, r => Assert.Equal(header.Length, Cells(r).Length));
    }

    [Fact]
    public void TextIsQuotedAndCannotRunAsAFormula()
    {
        Assert.Equal("\"'=cmd, \"\"quoted\"\"\"", MetricsExport.Cell("=cmd, \"quoted\""));
        Assert.Equal("'-1", MetricsExport.Cell("-1"));
        Assert.Equal("-1", MetricsExport.Cell(-1));
        Assert.Equal("0.1235", MetricsExport.Cell(0.123456));
        Assert.Equal("true", MetricsExport.Cell(true));
        Assert.Equal("", MetricsExport.Cell(null));
    }

    [Fact]
    public void NothingPrivateIsWritten()
    {
        var runs = new[] { Systematic(), Multivocal() };
        string all = MetricsExport.RunsCsv(runs) + MetricsExport.RunsJson(runs) + MetricsExport.StagesCsv(runs);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", all); // the whole id opens the run's archive
        Assert.DoesNotContain("fedcba9876543210fedcba9876543210", all);
        Assert.DoesNotContain("e026e4ed8f3b", all); // nor the hash of the research question
        Assert.DoesNotContain("QueryHash", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query_hash", all);
    }

    [Fact]
    public void ThePriceTableIsOffUnlessSetAndCountsCachedTokensAtTheirPrice()
    {
        var run = Systematic();
        Assert.Null(MetricsPrices.None.Cost(run));
        Assert.Null(new MetricsOptions().Prices.Cost(run));
        Assert.Contains(",,,", Lines(MetricsExport.RunsCsv(new[] { run }))[1]); // estimated_cost and cost_currency empty

        var prices = new MetricsPrices { Currency = "EUR", Models = { ["mistral-large-latest"] = new ModelPrice { InputPerMillion = 2, CachedInputPerMillion = 0.2, OutputPerMillion = 6 } } };
        Assert.Equal(Math.Round((100 * 2 + 100 * 0.2 + 100 * 6) / 1_000_000.0, 4), prices.Cost(run));
        Assert.Null(prices.Cost(new RunMetrics { CostByModel = new() { ["other"] = new StageCost { InputTokens = 5 } } })); // a model with no price
        Assert.Null(prices.Cost(Multivocal())); // not counted by model then
        Assert.Contains(",EUR,", Lines(MetricsExport.RunsCsv(new[] { run }, prices))[1]);
    }

    [Fact]
    public void TheJsonHasTheCsvColumnsTypedAndTheEffortByStageAndModel()
    {
        using var doc = JsonDocument.Parse(MetricsExport.RunsJson(new[] { Systematic(), Multivocal() }));
        var first = doc.RootElement[0];
        Assert.Equal(RunsHeader.Split(',').Concat(new[] { "cost_by_stage", "cost_by_model", "sources_by_search" }), first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(0.81, first.GetProperty("screening_kappa").GetDouble());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("grey_kappa").ValueKind);
        Assert.Equal(80, first.GetProperty("cost_by_stage").GetProperty("screening").GetProperty("ReasoningTokens").GetInt64());
        Assert.Equal(12, doc.RootElement[1].GetProperty("sources_by_search").GetProperty("stackexchange").GetInt32());
    }

    [Fact]
    public void StagesCsvHasARowPerStageAndPerModel()
    {
        var rows = Lines(MetricsExport.StagesCsv(new[] { Systematic() }));
        Assert.Equal(new[] { "01234567,systematic,0.5.0,stage,screening,2,1,0,,200,,100,80,300,90", "01234567,systematic,0.5.0,model,mistral-large-latest,2,,,,200,100,100,,300,0" }, rows[1..]);
    }

    [Fact]
    public void TheDataDictionaryDescribesEveryColumnInOrder()
    {
        string doc = File.ReadAllText(Path.Join(RepoRoot(), "docs", "metrics-dictionary.md"));
        var described = Regex.Matches(doc, @"^\| `([a-z0-9_]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var columns = MetricsExport.Columns().Select(c => c.Name).ToList();
        // The formal pool's shared columns are described once, under the grey pool's names.
        var expected = columns.Where(c => !(c.StartsWith("formal_", StringComparison.Ordinal) && columns.Contains("grey_" + c["formal_".Length..]))).ToList();
        Assert.Equal(expected, described.Take(expected.Count));
        Assert.Equal(MetricsExport.StageColumns, described.Skip(expected.Count));
    }

    private static string[] Cells(string line) => Regex.Matches(line + ",", "(\"(?:[^\"]|\"\")*\"|[^,]*),").Select(m => m.Groups[1].Value).ToArray();

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Join(dir, "AuBtechReviewAgent.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found");
    }
}
