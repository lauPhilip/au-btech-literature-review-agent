using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Citation counts from the sources, the seed rule (citations per year, ties) and the ranking of a seed's neighbours.</summary>
public class CitationSeedsTests
{
    [Fact]
    public void EachOpenSourceKeepsItsCitationCountAndNullWhenItGivesNone()
    {
        var openAlex = OpenAlexSource.ParseWorks("""{"results":[{"id":"https://openalex.org/W1","display_name":"A","publication_year":2020,"cited_by_count":42},{"id":"https://openalex.org/W2","display_name":"B"}]}""");
        Assert.Equal(new int?[] { 42, null }, openAlex.Select(p => p.CitedByCount));

        var s2 = SemanticScholarSource.Parse("""{"data":[{"paperId":"p1","title":"A","year":2021,"citationCount":7},{"paperId":"p2","title":"B"}]}""");
        Assert.Equal(new int?[] { 7, null }, s2.Select(p => p.CitedByCount));

        var crossref = CrossrefSource.Parse("""{"message":{"items":[{"DOI":"10.1000/a","title":["A"],"is-referenced-by-count":3},{"DOI":"10.1000/b","title":["B"]}]}}""");
        Assert.Equal(new int?[] { 3, null }, crossref.Select(p => p.CitedByCount));
    }

    [Fact]
    public void CitationsPerYearCountThePublicationYearAsOne()
    {
        Assert.Equal(100, CitationSeeds.PerYear(100, 2026, 2026));
        Assert.Equal(20, CitationSeeds.PerYear(100, 2022, 2026));
        Assert.Equal(5, CitationSeeds.PerYear(5, 2027, 2026)); // a date after the run counts as one year
    }

    [Fact]
    public void SeedsAreRankedByCitationsPerYearSoANewStudyCanBeatAnOldOne()
    {
        var record = CitationSeeds.Select(new[]
        {
            new SeedCandidate("old", "Old classic", 2006, 400, "10.1000/old"),   // 400 / 21 = 19.0 per year
            new SeedCandidate("new", "New hit", 2025, 60, "10.1000/new"),        // 60 / 2 = 30 per year
            new SeedCandidate("mid", "Middle", 2016, 110, "10.1000/mid"),        // 110 / 11 = 10 per year
        }, count: 2, perDirection: 20, referenceYear: 2026);

        Assert.Equal(new[] { "new", "old" }, record.Seeds.Select(s => s.PaperId));
        Assert.Equal(new[] { 1, 2 }, record.Seeds.Select(s => s.Rank));
        Assert.Equal(30, record.Seeds[0].CitationsPerYear);
        Assert.Equal(19.05, record.Seeds[1].CitationsPerYear);
        Assert.Equal((3, 0, 2026), (record.Candidates, record.WithoutCount, record.ReferenceYear));
    }

    [Fact]
    public void TiesGoToMoreCitationsThenTheNewerStudyThenTheRecordId()
    {
        var record = CitationSeeds.Select(new[]
        {
            new SeedCandidate("b", "B", 2025, 20, "10.1000/b"),   // 10 per year, 20 citations
            new SeedCandidate("a", "A", 2025, 20, "10.1000/a"),   // same: decided by id
            new SeedCandidate("c", "C", 2024, 30, "10.1000/c"),   // 10 per year, 30 citations: first
            new SeedCandidate("d", "D", 2026, 10, "10.1000/d"),   // 10 per year, 10 citations: last
        }, count: 10, perDirection: 20, referenceYear: 2026);

        Assert.Equal(new[] { "c", "a", "b", "d" }, record.Seeds.Select(s => s.PaperId));
    }

    [Fact]
    public void ARecordWithoutADoiCountOrYearCannotBeASeedAndIsCounted()
    {
        var record = CitationSeeds.Select(new[]
        {
            new SeedCandidate("ok", "Ok", 2020, 5, "10.1000/ok"),
            new SeedCandidate("nocount", "No count", 2020, null, "10.1000/n"),
            new SeedCandidate("noyear", "No year", 0, 50, "10.1000/y"),
            new SeedCandidate("nodoi", "No DOI", 2020, 500, null),
        }, count: 10, perDirection: 20, referenceYear: 2026);

        Assert.Equal("ok", Assert.Single(record.Seeds).PaperId);
        Assert.Equal(2, record.WithoutCount); // the record without a DOI was never a candidate
    }

    [Fact]
    public void NeighboursAreRankedByCitationsPerYearAndUnrankableOnesComeLast()
    {
        var pool = new List<AcademicPaper>
        {
            Paper("W1", 2000, 260),  // 10 per year
            Paper("W2", null, 999),  // no year: last
            Paper("W3", 2025, 40),   // 20 per year
            Paper("W4", 2016, null), // no count: last, after W2
            Paper("W5", 2021, 30),   // 5 per year
        };
        var top = CitationSeeds.TopNeighbours(pool, 4, 2026);
        Assert.Equal(new[] { "W3", "W1", "W5", "W2" }, top.Select(p => p.Id));
    }

    [Fact]
    public async Task BuildingOutKeepsTheTopNeighboursPerDirectionAndNotesAFailedSeed()
    {
        var record = CitationSeeds.Select(new[]
        {
            new SeedCandidate("s1", "Seed one", 2024, 90, "10.1000/s1"),
            new SeedCandidate("s2", "Seed two", 2024, 30, "10.1000/fail"),
        }, count: 2, perDirection: 1, referenceYear: 2026);
        var graph = new PoolGraph();

        var results = await CitationSeeds.BuildOutAsync(graph, record, m => m);

        Assert.Equal(new[] { "10.1000/s1", "10.1000/fail" }, graph.Asked);
        Assert.Equal(CitationSeeds.PoolPerDirection, graph.PoolAsked);
        Assert.Equal("W3", Assert.Single(results[0].Backward).Id);
        Assert.Equal("W5", Assert.Single(results[0].Forward).Id);
        Assert.Equal((1, 1), (record.Seeds[0].Backward, record.Seeds[0].Forward));
        Assert.Equal("no such work", record.Seeds[1].Error);
        Assert.Empty(results[1].Backward);
        Assert.Equal(2, record.Identified);
    }

    private static AcademicPaper Paper(string id, int? year, int? cites) =>
        new(id, "Title " + id, "", year == null ? "Published: " : $"Published: {year}", new(), "Venue", CitedByCount: cites);

    private sealed class PoolGraph : ICitationGraph
    {
        public List<string> Asked { get; } = new();
        public int PoolAsked { get; private set; }
        public string SourceName => "OpenAlex";
        public Task<CitationNeighbours> GetCitationNeighboursAsync(string doi, int perDirection) => throw new InvalidOperationException("seeds use the pool");
        public Task<CitationNeighbours> GetCitationPoolAsync(string doi, int poolPerDirection)
        {
            Asked.Add(doi);
            PoolAsked = poolPerDirection;
            if (doi.Contains("fail")) throw new InvalidOperationException("no such work");
            return Task.FromResult(new CitationNeighbours(
                new() { Paper("W1", 2000, 260), Paper("W3", 2025, 40) },
                new() { Paper("W4", 2016, 10), Paper("W5", 2026, 9) },
                new[] { "{}" }));
        }
    }
}
