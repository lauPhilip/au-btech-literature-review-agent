using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>One seed study: an included record chosen in code for its citations per year, and what building out from it found.</summary>
public sealed class CitationSeed
{
    public int Rank { get; set; }
    public string PaperId { get; set; } = "";
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public int CitedByCount { get; set; }
    public double CitationsPerYear { get; set; }
    public string Doi { get; set; } = "";
    /// <summary>Referenced works kept for screening (before duplicate and year checks).</summary>
    public int Backward { get; set; }
    /// <summary>Citing works kept for screening (before duplicate and year checks).</summary>
    public int Forward { get; set; }
    public string? Error { get; set; }
}

/// <summary>An included record that could become a seed.</summary>
public sealed record SeedCandidate(string PaperId, string Title, int Year, int? CitedByCount, string? Doi);

/// <summary>The seed step of one run, on file: the rule's numbers, the seeds with their counts and ranks, and what was found.</summary>
public sealed class SeedChainingRecord
{
    /// <summary>The year citations per year are counted up to: the year of the run.</summary>
    public int ReferenceYear { get; set; }
    public int SeedsWanted { get; set; }
    public int PerDirection { get; set; }
    public int PoolPerDirection { get; set; }
    /// <summary>Included records with a DOI, a citation count and a year, from which the seeds were ranked.</summary>
    public int Candidates { get; set; }
    /// <summary>Included records with a DOI that could not be ranked, because their source gave no citation count or year.</summary>
    public int WithoutCount { get; set; }
    /// <summary>Records identified by building out from the seeds, before duplicate and year checks.</summary>
    public int Identified { get; set; }
    public List<CitationSeed> Seeds { get; set; } = new();
}

/// <summary>What building out from one seed returned: the ranked neighbours kept and every raw answer they came from.</summary>
public sealed record SeedNeighbours(CitationSeed Seed, List<AcademicPaper> Backward, List<AcademicPaper> Forward, IReadOnlyList<string> RawResponses);

/// <summary>
/// The seed step shared by the systematic review and the multivocal review's formal pool. Seeds are chosen in code, never
/// by a model: the included records ranked by citations per year since publication (the count divided by the years since
/// publication, the publication year counting as one), so a recent study can compete with an old one. Each seed is built
/// out one round in both directions, and its neighbours are ranked the same way.
/// </summary>
public static class CitationSeeds
{
    /// <summary>The neighbours fetched per seed and direction before ranking.</summary>
    public const int PoolPerDirection = OpenAlexSource.MaxPoolPerDirection;

    public static double PerYear(int citedBy, int year, int referenceYear) => citedBy / (double)Math.Max(1, referenceYear - year + 1);

    /// <summary>
    /// The seed rule: from the candidates with a DOI, a citation count and a year, the <paramref name="count"/> with the
    /// most citations per year; ties go to more citations, then the newer study, then the record id, so every run of the
    /// same ledger picks the same seeds.
    /// </summary>
    public static SeedChainingRecord Select(IEnumerable<SeedCandidate> included, int count, int perDirection, int referenceYear)
    {
        var withDoi = included.Where(c => PrismaReviewEngine.NormalizeDoi(c.Doi) != null).ToList();
        var rankable = withDoi.Where(c => c.CitedByCount != null && c.Year > 0).ToList();
        var record = new SeedChainingRecord
        {
            ReferenceYear = referenceYear,
            SeedsWanted = count,
            PerDirection = perDirection,
            PoolPerDirection = PoolPerDirection,
            Candidates = rankable.Count,
            WithoutCount = withDoi.Count - rankable.Count,
        };
        record.Seeds = rankable
            .OrderByDescending(c => PerYear(c.CitedByCount!.Value, c.Year, referenceYear))
            .ThenByDescending(c => c.CitedByCount!.Value)
            .ThenByDescending(c => c.Year)
            .ThenBy(c => c.PaperId, StringComparer.Ordinal)
            .Take(Math.Max(0, count))
            .Select((c, i) => new CitationSeed
            {
                Rank = i + 1,
                PaperId = c.PaperId,
                Title = c.Title,
                Year = c.Year,
                CitedByCount = c.CitedByCount!.Value,
                CitationsPerYear = Math.Round(PerYear(c.CitedByCount!.Value, c.Year, referenceYear), 2),
                Doi = PrismaReviewEngine.NormalizeDoi(c.Doi)!,
            })
            .ToList();
        return record;
    }

    /// <summary>
    /// The <paramref name="take"/> neighbours with the most citations per year, ranked like the seeds. A work without a
    /// citation count or a year cannot be ranked, so it comes after those that can, in the order the graph gave it.
    /// </summary>
    public static List<AcademicPaper> TopNeighbours(IEnumerable<AcademicPaper> pool, int take, int referenceYear)
    {
        var list = pool.Select((p, i) => (Paper: p, Index: i, Year: ApaCitationBuilder.ExtractYear(p.PublishedDate))).ToList();
        var ranked = list.Where(x => x.Paper.CitedByCount != null && x.Year > 0)
            .OrderByDescending(x => PerYear(x.Paper.CitedByCount!.Value, x.Year, referenceYear))
            .ThenByDescending(x => x.Paper.CitedByCount!.Value)
            .ThenByDescending(x => x.Year)
            .ThenBy(x => x.Paper.Id, StringComparer.Ordinal);
        var unranked = list.Where(x => x.Paper.CitedByCount == null || x.Year <= 0).OrderBy(x => x.Index);
        return ranked.Concat(unranked).Take(Math.Max(0, take)).Select(x => x.Paper).ToList();
    }

    /// <summary>
    /// Builds out one round from every seed, one seed after another so OpenAlex is asked politely: a pool of references
    /// and citing works per seed, ranked in code, the top <see cref="SeedChainingRecord.PerDirection"/> of each kept.
    /// A failed look-up is noted on its seed and the others go on.
    /// </summary>
    public static async Task<List<SeedNeighbours>> BuildOutAsync(ICitationGraph graph, SeedChainingRecord record, Func<string, string> sanitize, Action<int>? seedDone = null)
    {
        var results = new List<SeedNeighbours>();
        foreach (var seed in record.Seeds)
        {
            try
            {
                var pool = await graph.GetCitationPoolAsync(seed.Doi, record.PoolPerDirection);
                var backward = TopNeighbours(pool.Backward, record.PerDirection, record.ReferenceYear);
                var forward = TopNeighbours(pool.Forward, record.PerDirection, record.ReferenceYear);
                seed.Backward = backward.Count;
                seed.Forward = forward.Count;
                results.Add(new SeedNeighbours(seed, backward, forward, pool.RawResponses));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                seed.Error = sanitize(ex.Message);
                results.Add(new SeedNeighbours(seed, new(), new(), Array.Empty<string>()));
            }
            seedDone?.Invoke(results.Count);
        }
        record.Identified = record.Seeds.Sum(s => s.Backward + s.Forward);
        return results;
    }
}
