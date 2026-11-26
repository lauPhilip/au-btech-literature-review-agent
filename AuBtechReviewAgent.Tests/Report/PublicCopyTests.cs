using System.Text.Json;
using System.Text.Json.Nodes;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>What a public copy (the gallery's, the deposit archive's) keeps of abstracts and quotes.</summary>
public class PublicCopyTests
{
    private static string Words(int n, string word = "word") => string.Join(" ", Enumerable.Range(1, n).Select(i => $"{word}{i}"));

    [Fact]
    public void AbstractsOfLicensedDatabasesGoAndArxivAbstractsStay()
    {
        var ledger = new ReviewState();
        ledger.Phases.Screening.Add(new ScreeningLog("SCOPUS_ID:123", "A", "Included", "r", "c", "s", Abstract: "Scopus abstract text."));
        ledger.Phases.Screening.Add(new ScreeningLog("IEEE_99", "B", "Excluded", "r", "c", "s", Abstract: "IEEE abstract text."));
        ledger.Phases.Screening.Add(new ScreeningLog("OPENALEX_W1", "C", "Excluded", "r", "c", "s", Abstract: "OpenAlex abstract text."));
        ledger.Phases.Screening.Add(new ScreeningLog("http://arxiv.org/abs/2401.00001v1", "D", "Included", "r", "c", "s", Abstract: "arXiv abstract text."));
        ledger.Phases.Screening.Add(new ScreeningLog("S2_abc", "E", "Included", "r", "c", "s", Doi: "10.48550/arXiv.2401.00002", Abstract: "Same arXiv abstract."));
        var copy = new PublicCopy();

        string json = copy.Redact("transparent-process.json", JsonSerializer.Serialize(ledger))!;
        var back = JsonSerializer.Deserialize<ReviewState>(json)!;

        Assert.Equal(new[] { "", "", "", "arXiv abstract text.", "Same arXiv abstract." }, back.Phases.Screening.Select(s => s.Abstract));
        Assert.Equal(3, copy.Record.AbstractsRemoved);
        Assert.Equal(2, copy.Record.AbstractsKept);
        Assert.DoesNotContain("Scopus abstract", json);
    }

    [Fact]
    public void AQuoteKeepsFiftyWordsWithAnEllipsisAndANoteAndItsFingerprint()
    {
        var copy = new PublicCopy();
        string full = Words(80);

        string shown = copy.Shorten("reference 1", full, "extraction.json", "$[0].Method.Quote");

        Assert.StartsWith(Words(50) + " " + PublicCopy.Ellipsis, shown);
        Assert.EndsWith(PublicCopy.ShortenedNote, shown);
        var record = Assert.Single(copy.Record.Shortened);
        Assert.Equal((80, 50), (record.Words, record.KeptWords));
        Assert.Equal(PublicCopy.Sha256(full), record.Sha256);
        Assert.Equal("$[0].Method.Quote", record.Path);
    }

    [Fact]
    public void OneSourceIsQuotedForAtMostThreeHundredWordsAndOtherSourcesHaveTheirOwnBudget()
    {
        var copy = new PublicCopy();
        var kept = Enumerable.Range(1, 8).Select(i => copy.Shorten("reference 1", Words(45, $"q{i}w"), "citation-audit.json", $"$.SupportChecks[{i}].Quote")).ToList();

        Assert.Equal(Words(45, "q1w"), kept[0]); // six quotes of 45 words fit (270 words)
        Assert.Equal(Words(45, "q6w"), kept[5]);
        Assert.StartsWith(Words(30, "q7w") + " " + PublicCopy.Ellipsis, kept[6]); // 30 words are left
        Assert.StartsWith(PublicCopy.Ellipsis + " ", kept[7]); // nothing is left
        Assert.Equal(Words(45, "x"), copy.Shorten("reference 2", Words(45, "x"), "citation-audit.json", "$.SupportChecks[9].Quote"));
    }

    [Fact]
    public void TheSameQuoteInTwoFilesIsShortenedTheSameWayAndCountedOnce()
    {
        var copy = new PublicCopy();
        string quote = Words(200);

        string first = copy.Shorten("reference 3", quote, "extraction.json", "$[0].Sample.Quote");
        string second = copy.Shorten("reference 3", quote, "transparent-process.json", "$.Extractions[0].Sample.Quote");

        Assert.Equal(first, second);
        Assert.Equal(2, copy.Record.QuotesShortened);
        Assert.Equal(Words(50, "y"), copy.Shorten("reference 3", Words(50, "y"), "extraction.json", "$[1].Sample.Quote")); // 250 words left
    }

    [Fact]
    public void QuotesAreFoundInExtractionsAuditsAndCodebooksByTheirReferenceNumber()
    {
        var extraction = new StudyExtraction
        {
            ReferenceNumber = 4, PaperId = "SCOPUS_ID:1",
            Method = new EvidencedValue { Value = "survey", Quote = Words(70), QuoteVerified = true },
            Appraisal = new() { new AppraisalAnswer { Id = "S1", Answer = "yes", Quote = Words(10) } },
        };
        var audit = new { SupportChecks = new[] { new CitationSupportResult { Reference = 4, Quote = Words(60), SecondQuote = Words(5) } } };
        var copy = new PublicCopy();

        var ex = JsonSerializer.Deserialize<List<StudyExtraction>>(copy.Redact("extraction.json", JsonSerializer.Serialize(new[] { extraction }))!)!.Single();
        var check = JsonNode.Parse(copy.Redact("citation-audit.json", JsonSerializer.Serialize(audit))!)!["SupportChecks"]![0]!;

        Assert.Contains(PublicCopy.ShortenedNote, ex.Method.Quote);
        Assert.True(ex.Method.QuoteVerified);
        Assert.Equal(Words(10), ex.Appraisal[0].Quote);
        Assert.Contains(PublicCopy.ShortenedNote, check["Quote"]!.GetValue<string>());
        Assert.Equal(Words(5), check["SecondQuote"]!.GetValue<string>());
        Assert.All(copy.Record.Shortened, s => Assert.Equal("reference 4", s.Source));
    }

    [Fact]
    public void AFileWithNothingToChangeIsReturnedAsItWasAndBrokenJsonIsRefused()
    {
        var copy = new PublicCopy();
        string json = "{\"Title\":\"x\",\"Abstract\":\"\"}";

        Assert.Same(json, copy.Redact("prisma-report.json", json));
        Assert.Null(copy.Redact("broken.json", "{ not json"));
    }

    [Fact]
    public void AFolderIsMadePublicInPlaceWithItsRecord()
    {
        string folder = Path.Join(Path.GetTempPath(), "public-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Join(folder, "extraction.json"), JsonSerializer.Serialize(new[] { new StudyExtraction { ReferenceNumber = 1, Method = new() { Quote = Words(90) } } }));
            File.WriteAllText(Path.Join(folder, "entry.json"), "{\"Quote\":\"" + Words(90) + "\"}");
            File.WriteAllText(Path.Join(folder, "broken.json"), "{");

            var record = PublicCopy.ApplyToFolder(folder, "entry.json");

            Assert.Equal(1, record.QuotesShortened);
            var stored = JsonSerializer.Deserialize<List<StudyExtraction>>(File.ReadAllText(Path.Join(folder, "extraction.json")))!;
            Assert.EndsWith(PublicCopy.ShortenedNote, stored[0].Method.Quote);
            Assert.Contains(Words(90), File.ReadAllText(Path.Join(folder, "entry.json")));
            Assert.False(File.Exists(Path.Join(folder, "broken.json")));
            Assert.True(File.Exists(Path.Join(folder, PublicCopy.RecordFile)));
        }
        finally { TestFolders.TryDelete(folder); }
    }
}
