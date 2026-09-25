#pragma warning disable SKEXP0070
using AuBtechReviewAgent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Net;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class ExportAndEvaluationTests
{
    private static IncludedPaperMetricRow Row(int n, string venueType, string category, params string[] authors) => new()
    {
        ReferenceNumber = n,
        Title = "LLMoxie: Exploring Agentic AI & Science",
        Authors = authors.ToList(),
        Year = 2026,
        VenueType = venueType,
        VenueName = venueType == "Journals" ? "Future Internet" : "",
        Category = category,
        Doi = "10.48550/arXiv.2607.02703",
        Url = "https://arxiv.org/abs/2607.02703v1",
        Summary = "A platform.",
    };

    [Fact]
    public void BibTeXHasTypedEntriesUniqueKeysAndEscaping()
    {
        string bib = BibliographyExporter.ToBibTeX(new[]
        {
            Row(2, "Journals", "Scholar", "Lucas Setiawan"),
            Row(1, "Preprints", "Arxiv", "Lucas Setiawan", "Anshul Mittal"),
        });

        Assert.Contains("@misc{setiawan2026llmoxie,", bib);
        Assert.Contains("@article{setiawan2026llmoxiea,", bib);
        Assert.Contains("author = {Setiawan, Lucas and Mittal, Anshul}", bib);
        Assert.Contains(@"title = {{LLMoxie: Exploring Agentic AI \& Science}}", bib);
        Assert.Contains("journal = {Future Internet}", bib);
        Assert.Contains("doi = {10.48550/arXiv.2607.02703}", bib);
        Assert.True(bib.IndexOf("@misc") < bib.IndexOf("@article")); // reference-number order
    }

    [Fact]
    public void RisUsesStandardTags()
    {
        string ris = BibliographyExporter.ToRis(new[] { Row(1, "Journals", "Scholar", "Ab, C.", "Jane Doe") });

        Assert.Contains("TY  - JOUR", ris);
        Assert.Contains("AU  - Ab, C.", ris);
        Assert.Contains("AU  - Doe, Jane", ris);
        Assert.Contains("PY  - 2026", ris);
        Assert.Contains("DO  - 10.48550/arXiv.2607.02703", ris);
        Assert.Contains("ER  - ", ris);
    }

    [Fact]
    public void CsvParserHandlesQuotesCommasAndNewlines()
    {
        var rows = ScreeningEvaluation.ParseCsv("﻿title,abstract,label_included\r\n\"A, B\",\"He said \"\"hi\"\"\nsecond line\",1\r\nPlain,,0\r\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("title", rows[0][0]);
        Assert.Equal("A, B", rows[1][0]);
        Assert.Equal("He said \"hi\"\nsecond line", rows[1][1]);
        Assert.Equal("0", rows[2][2]);
    }

    [Fact]
    public void LoadsAsreviewCsvAndSkipsUnlabelledRows()
    {
        var records = ScreeningEvaluation.LoadCsv("record_id,title,abstract,label_included\n7,T1,A1,1\n8,T2,A2,0\n9,T3,A3,\n");

        Assert.Equal(2, records.Count);
        Assert.True(records[0].Included);
        Assert.Equal("7", records[0].Id);
        Assert.False(records[1].Included);
    }

    [Fact]
    public void MetricsMatchAHandComputedExample()
    {
        // 8 TP, 2 FN, 5 FP, 85 TN
        var pairs = Enumerable.Repeat((true, true), 8)
            .Concat(Enumerable.Repeat((true, false), 2))
            .Concat(Enumerable.Repeat((false, true), 5))
            .Concat(Enumerable.Repeat((false, false), 85));

        var m = ScreeningConfusion.From(pairs);

        Assert.Equal(0.8, m.Recall, 3);
        Assert.Equal(8.0 / 13, m.Precision, 3);
        Assert.Equal(85.0 / 90, m.Specificity, 3);
        // po = 0.93, pe = 0.13*0.10 + 0.87*0.90 = 0.796 -> kappa = 0.134/0.204
        Assert.Equal(0.134 / 0.204, m.CohensKappa, 3);
    }

    [Fact]
    public void SampleKeepsAllRelevantRecordsAndIsReproducible()
    {
        var records = Enumerable.Range(0, 100).Select(i => new LabelledRecord($"{i}", $"T{i}", "", i < 5)).ToList();

        var (a, weight) = ScreeningEvaluation.Sample(records, 19, seed: 7);
        var (b, _) = ScreeningEvaluation.Sample(records, 19, seed: 7);

        Assert.Equal(24, a.Count);
        Assert.Equal(5, a.Count(r => r.Included));
        Assert.Equal(95.0 / 19, weight, 3);
        Assert.Equal(a.Select(r => r.Id), b.Select(r => r.Id));
    }

    [Fact]
    public async Task RecorderRetriesRateLimitsAndLogsTheStage()
    {
        var fake = new FakeChatService()
            .Throws(new HttpOperationException(HttpStatusCode.TooManyRequests, null, "rate limited", null))
            .Returns("{\"ok\":true}");
        var recorder = new RecordingChatCompletionService(fake, "mistral-large-latest", backoff: _ => TimeSpan.Zero);

        using (LlmStage.Begin("screening"))
        {
            var result = await recorder.GetChatMessageContentAsync("prompt text",
                new Microsoft.SemanticKernel.Connectors.MistralAI.MistralAIPromptExecutionSettings { Temperature = 0.0 });
            Assert.Equal("{\"ok\":true}", result.Content);
        }

        var call = Assert.Single(recorder.Calls);
        Assert.Equal("screening", call.Stage);
        Assert.Equal(2, call.Attempts);
        Assert.Equal("ok", call.Outcome);
        Assert.Equal(0.0, call.Temperature);
        Assert.Equal("fake-model", call.ModelReported);
        Assert.Equal(RecordingChatCompletionService.Sha256("prompt text"), call.PromptSha256);

        var settings = recorder.Summarize("1.0.0+abc");
        Assert.Equal(1, settings.LlmRetries);
        Assert.Equal(0.0, settings.StageTemperatures["screening"]);
    }

    [Fact]
    public async Task RecorderDoesNotRetryPermanentErrors()
    {
        var fake = new FakeChatService().Throws(new HttpOperationException(HttpStatusCode.Unauthorized, null, "bad key", null));
        var recorder = new RecordingChatCompletionService(fake, "m", backoff: _ => TimeSpan.Zero);

        await Assert.ThrowsAsync<HttpOperationException>(() => recorder.GetChatMessageContentAsync("p"));

        var call = Assert.Single(recorder.Calls);
        Assert.Equal(1, call.Attempts);
        Assert.StartsWith("failed", call.Outcome);
    }
}
