using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The deposit archive for Zenodo or OSF: what it shares of the owner's archive, and that it checks out on its own.</summary>
public class DepositArchiveTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), "deposit-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _runId = Guid.NewGuid();
    private static string Words(int n) => string.Join(" ", Enumerable.Range(1, n).Select(i => $"w{i}"));

    public DepositArchiveTests()
    {
        string raw = Path.Join(_folder, RunArchive.RawResponsesFolder);
        Directory.CreateDirectory(raw);
        var state = new ReviewState();
        state.Phases.Screening.Add(new ScreeningLog("SCOPUS_ID:1", "Closed paper", "Included", "r", "c", "s", Doi: "10.1/closed", Abstract: "A Scopus abstract."));
        state.Phases.Screening.Add(new ScreeningLog("SCOPUS_ID:2", "Open paper as Scopus titles it", "Included", "r", "c", "s", Doi: "10.1/OPEN", Abstract: "Another Scopus abstract."));
        File.WriteAllText(Path.Join(_folder, "transparent-process.json"), JsonSerializer.Serialize(state));
        File.WriteAllText(Path.Join(_folder, "extraction.json"), JsonSerializer.Serialize(new[] { new StudyExtraction { ReferenceNumber = 1, Method = new() { Quote = Words(90) } } }));
        File.WriteAllText(Path.Join(_folder, "prisma-report.json"), JsonSerializer.Serialize(new PrismaReport { TitleItem = "A review" }));
        File.WriteAllText(Path.Join(_folder, "llm-calls.json"), "[{\"Prompt\":\"full text of a paper\"}]");
        File.WriteAllText(Path.Join(_folder, "protocol.md"), "# Review protocol");
        foreach (var title in new[] { "Closed paper", "Open paper as Scopus titles it", "Paper Unpaywall knows" })
            File.WriteAllText(Path.Join(_folder, DocumentRAGUtility.PdfFileName(title)), "%PDF-1.4 " + title);
        // Named by key since #587, with its record beside it: an open paper with a DOI, and a closed one.
        foreach (var (id, doi) in new[] { ("SCOPUS_ID:3", "10.1/keyed-open"), ("SCOPUS_ID:4", "10.1/keyed-closed") })
        {
            File.WriteAllText(Path.Join(_folder, FullTextFile.PdfName(id, doi)), "%PDF-1.4 " + id);
            File.WriteAllText(Path.Join(_folder, FullTextFile.RecordName(id, doi)), "{}");
        }
        File.WriteAllText(Path.Join(raw, "01-openalex-q1.json"),
            "{\"results\":[{\"id\":\"https://openalex.org/W3\",\"doi\":\"https://doi.org/10.1/keyed-open\",\"display_name\":\"Keyed\",\"best_oa_location\":{\"license\":\"cc-by\"}}," +
            "{\"id\":\"https://openalex.org/W1\",\"doi\":\"https://doi.org/10.1/open\",\"display_name\":\"Open paper\",\"best_oa_location\":{\"license\":\"cc-by\"}}," +
            "{\"id\":\"https://openalex.org/W2\",\"doi\":\"https://doi.org/10.1/closed\",\"display_name\":\"Closed paper\",\"best_oa_location\":{\"license\":\"publisher-specific-oa\"}}]}");
        File.WriteAllText(Path.Join(raw, "unpaywall-0123456789abcdef.json"), "{\"doi\":\"10.1/u\",\"title\":\"Paper Unpaywall knows\",\"best_oa_location\":{\"license\":\"cc-by-nc\",\"url_for_pdf\":\"https://x.org/u.pdf\"}}");
        File.WriteAllText(Path.Join(raw, "02-sciencedirect-api-q1.json"), "{\"search-results\":{}}");
        File.WriteAllText(Path.Join(raw, "03-ieee-xplore-api-q1.json"), "{}");
        File.WriteAllText(Path.Join(raw, "04-semantic-scholar-q1.json"), "{}");
    }

    public void Dispose() => TestFolders.TryDelete(_folder);

    private (Dictionary<string, string> Files, DepositRecord Record, MemoryStream Zip) Deposit()
    {
        var generated = new List<(string, byte[])> { ("main.tex", Encoding.UTF8.GetBytes("\\documentclass{article}")) };
        byte[] owner = RunArchive.Build(_runId, _folder, generated, ReviewModules.Default.ArchiveFiles, "protocol-hash", "model");
        var output = new MemoryStream();
        var record = DepositArchive.Write(new MemoryStream(owner), output);
        output.Position = 0;
        using var zip = new ZipArchive(new MemoryStream(output.ToArray()));
        var files = zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
        return (files, record, output);
    }

    [Fact]
    public void PapersWithoutAnOpenLicenceAndLicensedAnswersStayInTheOwnersArchiveOnly()
    {
        var (files, record, _) = Deposit();

        Assert.Contains("SourcePapers/" + DocumentRAGUtility.PdfFileName("Open paper as Scopus titles it"), files.Keys); // by its DOI in the ledger
        Assert.Contains("SourcePapers/" + DocumentRAGUtility.PdfFileName("Paper Unpaywall knows"), files.Keys); // by Unpaywall's licence
        Assert.DoesNotContain("SourcePapers/" + DocumentRAGUtility.PdfFileName("Closed paper"), files.Keys);
        Assert.Contains("SourcePapers/" + FullTextFile.PdfName("SCOPUS_ID:3", "10.1/keyed-open"), files.Keys); // named by its DOI (#587)
        Assert.Contains("SourcePapers/" + FullTextFile.RecordName("SCOPUS_ID:3", "10.1/keyed-open"), files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.Contains(FullTextFile.PdfName("SCOPUS_ID:4", "10.1/keyed-closed")[..^4]));
        Assert.Contains("SourceResponses/01-openalex-q1.json", files.Keys);
        Assert.Contains("SourceResponses/unpaywall-0123456789abcdef.json", files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.Contains("sciencedirect") || k.Contains("ieee") || k.Contains("semantic"));
        Assert.DoesNotContain("llm-calls.json", files.Keys);
        Assert.Contains("main.tex", files.Keys);
        Assert.Contains("protocol.md", files.Keys);
        Assert.Equal(7, record.LeftOut.Count); // with the closed keyed PDF and its record
        Assert.All(record.LeftOut, l => Assert.Matches("^[0-9a-f]{64}$", l.Sha256!));
        Assert.Equal(4, record.PapersKept.Count);
    }

    [Fact]
    public void TheLedgerAndQuotesFollowThePublicCopyRulesAndTheOwnersFingerprintsAreKept()
    {
        var (files, record, _) = Deposit();

        var ledger = JsonSerializer.Deserialize<ReviewState>(files["transparent-process.json"])!;
        Assert.All(ledger.Phases.Screening, s => Assert.Equal("", s.Abstract));
        var extraction = JsonSerializer.Deserialize<List<StudyExtraction>>(files["extraction.json"])!;
        Assert.EndsWith(PublicCopy.ShortenedNote, extraction[0].Method.Quote);
        Assert.Equal(new[] { "extraction.json", "transparent-process.json" }, record.Changed.Select(c => c.Path).OrderBy(p => p));
        Assert.Equal(2, record.PublicCopy.AbstractsRemoved);
        Assert.Equal(_runId.ToString(), record.RunId);
        Assert.Equal(files["prisma-report.json"], File.ReadAllText(Path.Join(_folder, "prisma-report.json"))); // unchanged files are copied as they are
        Assert.Contains(DepositArchive.RecordFile, files.Keys);
        Assert.Contains("deposit copy", files[DepositArchive.ReadmeFile]);
    }

    [Fact]
    public void TheDepositHasItsOwnManifestThatChecksOut()
    {
        var (files, _, zip) = Deposit();

        var check = RunManifest.CheckArchive(zip);

        Assert.True(check.Unchanged);
        Assert.Equal(_runId.ToString(), check.RunId);
        Assert.Equal("protocol-hash", check.ProtocolHash);
        Assert.Equal(files.Count - 1, check.FilesListed);
    }

    [Theory]
    [InlineData("01-openalex-q1.json", true)]
    [InlineData("02-arxiv-api-q2-1.xml", true)]
    [InlineData("03-crossref-q1.json", true)]
    [InlineData("seed-01.json", true)]
    [InlineData("chain-03.json", true)]
    [InlineData("reference-crossref-0123456789abcdef.json", true)]
    [InlineData("unpaywall-0123456789abcdef.json", true)]
    [InlineData("04-sciencedirect-api-q1.json", false)]
    [InlineData("05-ieee-xplore-api-q1.json", false)]
    [InlineData("06-semantic-scholar-q1.json", false)]
    [InlineData("grey-github-1.json", false)]
    public void OnlyAnswersOfOpenServicesAreShared(string name, bool open) => Assert.Equal(open, DepositLicences.IsOpenAnswer(name));

    [Theory]
    [InlineData("cc-by", true)]
    [InlineData("cc0", true)]
    [InlineData("CC BY-SA 4.0", true)]
    [InlineData("public-domain", true)]
    [InlineData("publisher-specific-oa", false)]
    [InlineData("other-oa", false)]
    [InlineData(null, false)]
    public void OnlyCreativeCommonsAndPublicDomainCountAsOpen(string? licence, bool open) => Assert.Equal(open, DepositLicences.IsOpenLicence(licence));
}
