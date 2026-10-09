using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The evidence of a multivocal run in the shared shape (MLR G-7, module design M7), for the shared writing steps:
/// the extracted sources numbered as in the source repository, each with its reference line, a profile line (kind,
/// outlet tier, quality, year), its quote-checked findings and its kept page text; and the themes of the questions the
/// model writes about, each with its sources and the values that put each source in it. Questions about frequency or
/// existence are left out: the paper answers them in code (<see cref="MultivocalPaperParts.AnsweredInCode"/>).
/// </summary>
public sealed record MultivocalPaperEvidence(ReviewEvidence Evidence, IReadOnlyDictionary<string, string> QuestionOfTheme);

public static class MultivocalEvidence
{
    /// <summary>How many characters of kept page text go into one chunk, for excerpts and the citation check.</summary>
    public const int ChunkCharacters = 1200;

    /// <summary>
    /// Builds the evidence from the run's files. <paramref name="readText"/> gives the kept text of a page, or null
    /// when there is none or it no longer matches its fingerprint (<see cref="MultivocalPages.ReadText"/>).
    /// </summary>
    public static MultivocalPaperEvidence Build(MultivocalReportInput input, Func<PageSnapshot, string?> readText)
    {
        var numbers = MultivocalReporter.SourceNumbers(input.Extraction);
        var references = GreyReferences.Build(input.Ledger, input.Pages, input.Extraction).ToDictionary(r => r.Number);
        var records = input.Ledger.Sources.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Record);
        var points = input.Quality.Sources.GroupBy(q => q.Address).ToDictionary(g => g.Key, g => g.First().Points);
        var pages = input.Pages.Pages.GroupBy(p => MultivocalSearcher.AddressKey(p.Url)).ToDictionary(g => g.Key, g => g.First());
        var findingsBySource = input.Synthesis.Findings.GroupBy(f => f.Address).ToDictionary(g => g.Key, g => g.ToList());

        var sources = new List<EvidenceSource>();
        foreach (var extracted in input.Extraction.Sources.Where(s => numbers.ContainsKey(s.Address)))
        {
            int number = numbers[extracted.Address];
            var record = records.GetValueOrDefault(extracted.Address);
            var findings = findingsBySource.GetValueOrDefault(extracted.Address) ?? new List<SynthesisFinding>();
            var page = pages.GetValueOrDefault(extracted.Address);
            string? text = page is { NotKept: null } ? readText(page) : null;
            sources.Add(new EvidenceSource
            {
                Reference = number,
                Title = extracted.Title,
                ReferenceLine = references.TryGetValue(number, out var reference) ? GreyReferences.Line(reference) : null,
                Profile = Profile(record, points.GetValueOrDefault(extracted.Address), input.Quality.MaxPoints),
                Findings = findings.Select(f => new EvidenceFinding(f.Id, $"{f.Attribute}: {f.Value}", f.Quote)).ToList(),
                Codes = findings.Select(f => $"{f.Attribute}: {f.Value}").Distinct().ToList(),
                Text = new ReferencedPaper(number, extracted.Address, extracted.Title, record?.Summary ?? "", Chunks(extracted.Address, text)),
            });
        }

        var types = input.Planned.Plan.Numbered().ToDictionary(q => q.Number, q => q.Question.Type);
        var byId = input.Synthesis.Findings.ToDictionary(f => f.Id);
        var themes = new List<EvidenceTheme>();
        var questionOf = new Dictionary<string, string>();
        foreach (var theme in input.Synthesis.Themes.Where(t => !MultivocalPaperParts.AnsweredInCode(types.GetValueOrDefault(t.Question))))
        {
            var findings = theme.Findings.Where(byId.ContainsKey).Select(id => byId[id]).Where(f => numbers.ContainsKey(f.Address)).ToList();
            if (findings.Count == 0) continue;
            string id = $"T{themes.Count + 1}";
            themes.Add(new EvidenceTheme
            {
                Id = id,
                Name = theme.Name,
                Description = theme.Description,
                Sources = findings.Select(f => numbers[f.Address]).Distinct().OrderBy(n => n).ToList(),
                Codes = findings.GroupBy(f => numbers[f.Address])
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(f => $"{f.Attribute}: {f.Value}").Distinct().ToList()),
            });
            questionOf[id] = theme.Question;
        }

        var evidence = new ReviewEvidence
        {
            ReviewName = input.Planned.Plan.ReviewKind,
            SourceLabel = "SOURCE",
            Sources = sources,
            Themes = themes,
            BaseFacts = BaseFacts(input, records, points),
        };
        return new MultivocalPaperEvidence(evidence, questionOf);
    }

    /// <summary>One line on what kind of source it is, for the writer: kind, outlet tier, quality points and year.</summary>
    public static string Profile(GreyRecord? record, double points, int maxPoints)
    {
        string kind = record?.Kind ?? "";
        string label = MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == kind).Label ?? (kind.Length > 0 ? kind : "Grey source");
        return $"{label}; {Ordinal(MultivocalSynthesiser.TierOf(kind))}-tier outlet; quality {points.ToString("0.#", CultureInfo.InvariantCulture)} of {maxPoints} points; " +
               (record?.Published is DateTime published ? $"published {published.Year}" : "no date") + ".";
    }

    /// <summary>The kept page text cut into chunks at paragraph ends, numbered from 1, so the writer and the check can quote from it.</summary>
    public static IReadOnlyList<DocumentChunk> Chunks(string address, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<DocumentChunk>();
        var chunks = new List<DocumentChunk>();
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n").Select(p => Regex.Replace(p, @"\s+", " ").Trim()).Where(p => p.Length > 0))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 1 > ChunkCharacters)
            {
                chunks.Add(new DocumentChunk(address, chunks.Count + 1, current.ToString()));
                current.Clear();
            }
            // A paragraph longer than a chunk is cut at sentence ends where it can be.
            string rest = paragraph;
            while (rest.Length > ChunkCharacters)
            {
                int cut = rest.LastIndexOf(". ", ChunkCharacters, StringComparison.Ordinal);
                cut = cut > ChunkCharacters / 2 ? cut + 1 : ChunkCharacters;
                if (current.Length > 0) { chunks.Add(new DocumentChunk(address, chunks.Count + 1, current.ToString())); current.Clear(); }
                chunks.Add(new DocumentChunk(address, chunks.Count + 1, rest[..cut].Trim()));
                rest = rest[cut..].Trim();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(rest);
        }
        if (current.Length > 0) chunks.Add(new DocumentChunk(address, chunks.Count + 1, current.ToString()));
        return chunks;
    }

    /// <summary>Facts about the evidence base, counted in code, for the discussion's limitations.</summary>
    public static string BaseFacts(MultivocalReportInput input, IReadOnlyDictionary<string, GreyRecord> records, IReadOnlyDictionary<string, double> points)
    {
        var numbers = MultivocalReporter.SourceNumbers(input.Extraction);
        var addresses = numbers.Keys.ToList();
        int Tier(int t) => addresses.Count(a => MultivocalSynthesiser.TierOf(records.GetValueOrDefault(a)?.Kind ?? "") == t);
        var years = addresses.Select(a => records.GetValueOrDefault(a)?.Published?.Year).OfType<int>().ToList();
        int undated = addresses.Count - years.Count;
        var kinds = addresses.GroupBy(a => records.GetValueOrDefault(a)?.Kind ?? "")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {LowerFirst(MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == g.Key).Label ?? "other")}");
        double mean = addresses.Count == 0 ? 0 : addresses.Average(a => points.GetValueOrDefault(a));
        var lines = new List<string>
        {
            $"The results rest on {addresses.Count} grey sources, none of them peer reviewed: {string.Join(", ", kinds)}.",
            $"Outlet tiers (checklist item 8.1): {Tier(1)} from 1st-tier, {Tier(2)} from 2nd-tier and {Tier(3)} from 3rd-tier outlets.",
            $"Mean quality {mean.ToString("0.#", CultureInfo.InvariantCulture)} of {input.Quality.MaxPoints} points on the Table 7 checklist; the threshold was {input.Quality.Threshold}.",
            years.Count > 0
                ? $"Published between {years.Min()} and {years.Max()}{(undated > 0 ? $"; {undated} without a date" : "")}."
                : "No source states its publication date.",
            "Each source is read as its page was kept on the access date; pages change and may since have changed.",
            "The searches used free search interfaces only; no general web search engine was used and no one was contacted.",
            "A language model screened, scored, mapped, extracted and grouped the sources; every value rests on a quote found in the kept page text.",
        };
        return string.Join("\n", lines);
    }

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };
}
