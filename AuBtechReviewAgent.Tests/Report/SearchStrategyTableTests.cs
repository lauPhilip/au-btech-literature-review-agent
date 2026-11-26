using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The full search strategy per source (PRISMA 2020 item 7), written in code from the run's search log.</summary>
public class SearchStrategyTableTests
{
    private static List<PlatformSearchLog> Logs() => new()
    {
        new() { SourceName = "arXiv API", QueryUsed = "agent loops", Timestamp = "2026-11-20 10:00:00 UTC", PapersFound = 5, Status = "Completed Successfully", RawResponseFile = "SourceResponses/01-arxiv-q1.xml" },
        new() { SourceName = "arXiv API", QueryUsed = "agentic workflows & safety", Timestamp = "2026-11-20 10:00:05 UTC", PapersFound = 3, Status = "Completed Successfully", FromCache = true },
        new() { SourceName = "Crossref", QueryUsed = "agent loops", Timestamp = "2026-11-20 10:00:09 UTC", PapersFound = 0, Status = "Faulted / Refused Connection" },
        new() { SourceName = "OpenAlex (citation chaining)", QueryUsed = "references of and citations to doi:10.1/x", PapersFound = 4, Status = "Completed Successfully" },
    };

    [Fact]
    public void EverySearchStringIsListedAsSentWithItsSourceDayRecordsAndStatus()
    {
        string tex = SearchStrategyTable.Latex(Logs(), "tab:A.4", PaperLatex.Escape, maxResultsPerSource: 10, searchStrings: 2);

        Assert.Contains(@"\caption{Search strategy per source}\label{tab:A.4}", tex);
        Assert.Contains(@"arXiv API & agent loops & 2026-11-20 & 5 & Completed\\ \addlinespace", tex);
        Assert.Contains(@"arXiv API & agentic workflows \& safety & 2026-11-20 & 3 & Completed (reused)\\ \addlinespace", tex);
        Assert.Contains(@"Crossref & agent loops & 2026-11-20 & 0 & Failed\\ \addlinespace", tex);
        // Chaining is a look-up by DOI, not a search string: it is summarised in the note, not given a row.
        Assert.DoesNotContain("citation chaining) &", tex);
    }

    [Fact]
    public void TheNoteSaysHowEachSourceReceivedTheStringAndWhichLimitsApplied()
    {
        string note = SearchStrategyTable.Note(Logs(), maxResultsPerSource: 10, searchStrings: 2, yearFrom: 2020, yearTo: 2026, peerReviewOnly: true);

        Assert.Contains("arXiv searched all fields for the string", note);
        Assert.Contains("limited to journal articles, proceedings articles and posted content", note);
        Assert.Contains("Each search asked for at most 5 records", note);
        Assert.Contains("10 distinct records", note);
        Assert.Contains("records published outside 2020-2026 were removed after retrieval", note);
        Assert.Contains("not peer reviewed were removed before screening", note);
        Assert.Contains("Reused marks an answer taken from an earlier identical search", note);
        Assert.Contains("references of and citations to 1 included record by DOI in OpenAlex (4 records returned)", note);
        Assert.Contains("SourceResponses/", note);
    }

    [Fact]
    public void WithoutLimitsTheNoteSaysNoFilterWasSentAndAnUnknownSourceGetsThePlainString()
    {
        var logs = new List<PlatformSearchLog> { new() { SourceName = "New Index", QueryUsed = "q", Timestamp = "", Status = "Completed Successfully" } };

        string note = SearchStrategyTable.Note(logs, maxResultsPerSource: 0, searchStrings: 1);
        string tex = SearchStrategyTable.Latex(logs, "tab:A.2", PaperLatex.Escape, 0, 1);

        Assert.Contains("New Index received the string as it is", note);
        Assert.Contains("No date, language or document-type filter was sent", note);
        Assert.DoesNotContain("Citation chaining", note);
        Assert.Contains("New Index & q & -- & 0 & Completed", tex);
    }

    [Fact]
    public void ARunThatSearchedNoDatabaseHasNoTable()
    {
        Assert.Equal("", SearchStrategyTable.Latex(new List<PlatformSearchLog>(), "tab:A.2", PaperLatex.Escape, 10, 1));
    }
}
