using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// The formal pool's search (block I-2 of docs/roadmap/mlr-todo.md): the plan's academic databases, the systematic
/// review's gateways (<see cref="IAcademicSource"/>), searched with the formal search strings under the same stopping
/// rule as the grey searches. Every raw answer is saved as received with its SHA-256; records are de-duplicated by id,
/// DOI and title as the systematic review does, and every record that is not kept is on file with its reason. Each
/// source kept is marked formal, so the two pools are counted apart. A failing database is recorded and the others
/// still run; a database the server has no key for is listed as skipped.
/// </summary>
public sealed partial class MultivocalSearcher
{
    /// <summary>The kind of a formal source; the grey kinds are <see cref="MultivocalGuidelines.GreyTypes"/>.</summary>
    public const string FormalKind = "academic";

    /// <summary>A record with no http(s) address cannot be cited or checked (rule 7), so it is not kept.</summary>
    public const string NoWebAddress = "No web address";

    /// <summary>The address a formal source is known by: its DOI when it has one, else the database's http(s) link.</summary>
    public static string FormalAddress(AcademicPaper paper) =>
        PrismaReviewEngine.NormalizeDoi(paper.Doi) is { } doi ? $"https://doi.org/{doi}"
        : PrismaReviewEngine.IsWebAddress(paper.Url) ? paper.Url!.Trim()
        : PrismaReviewEngine.IsWebAddress(paper.PdfUrl) ? paper.PdfUrl!.Trim()
        : "";

    private async Task SearchFormalPoolAsync(Guid runId, string rawFolder, MultivocalPlan plan, int perSearch, MultivocalLedger ledger,
        Dictionary<string, FoundGreySource> byAddress, IProgress<string>? progress)
    {
        var strings = plan.EffectiveFormalSearchStrings;
        var removed = ledger.FormalRemoved = new List<RemovedRecord>();
        var seenIds = new Dictionary<string, FoundGreySource>(StringComparer.OrdinalIgnoreCase);
        var seenDois = new Dictionary<string, FoundGreySource>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new Dictionary<string, FoundGreySource>(StringComparer.Ordinal);
        int searchesInAll = plan.FormalDatabases.Count * strings.Count, searchNumber = 0;

        foreach (string key in plan.FormalDatabases)
        {
            string name = SourceCatalog.DisplayNameFor(key);
            var source = _academic?.Invoke(key);
            if (source == null)
            {
                ledger.Skipped.Add(new SkippedSearch(key, SourceCatalog.Find(key)?.KeyKind is SourceKeyKind.Elsevier or SourceKeyKind.Ieee
                    ? "Needs a key this server does not have."
                    : "Not available on this server."));
                continue;
            }

            for (int i = 0; i < strings.Count; i++)
            {
                string text = strings[i];
                progress?.Report($"{++searchNumber} of {searchesInAll} academic searches · {name}: \"{text}\"");
                var log = new GreySearchLog { Source = key, Formal = true, SearchString = text, Requested = perSearch, SearchedUtc = DateTime.UtcNow };
                ledger.Searches.Add(log);
                try
                {
                    var papers = await source.FetchPapersAsync(text, perSearch);
                    log.Returned = papers.Count;
                    await SaveFormalRawAsync(rawFolder, $"formal-{key}-{i + 1}", source.LastRawResponses, log);
                    string foundBy = $"{key}: {text}";
                    foreach (var paper in papers)
                    {
                        string doi = PrismaReviewEngine.NormalizeDoi(paper.Doi) ?? "";
                        string title = PrismaReviewEngine.NormalizeTitle(paper.Title);
                        string address = FormalAddress(paper);
                        var first = seenIds.GetValueOrDefault(paper.Id)
                            ?? (doi.Length > 0 ? seenDois.GetValueOrDefault(doi) : null)
                            ?? (title.Length > 0 ? seenTitles.GetValueOrDefault(title) : null)
                            ?? (address.Length > 0 ? byAddress.GetValueOrDefault(AddressKey(address)) : null);
                        if (first != null)
                        {
                            // The same work again: on file as a duplicate, as in the systematic review, and its search noted.
                            removed.Add(Removed(paper, name, text, RemovedRecord.Duplicate, first.Record.Id));
                            if (!first.FoundBy.Contains(foundBy)) first.FoundBy.Add(foundBy);
                            continue;
                        }
                        if (address.Length == 0)
                        {
                            removed.Add(Removed(paper, name, text, NoWebAddress));
                            continue;
                        }

                        var found = new FoundGreySource { Record = RecordOf(paper, address, name), FoundBy = { foundBy }, Paper = paper };
                        seenIds[paper.Id] = found;
                        if (doi.Length > 0) seenDois[doi] = found;
                        if (title.Length > 0) seenTitles[title] = found;
                        byAddress[AddressKey(address)] = found;
                        ledger.Sources.Add(found);
                        log.New++;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One failing database does not stop the others; the ledger says what failed, without any key.
                    log.Error = PrismaReviewEngine.SanitizeLogMessage($"{ex.GetType().Name}: {ex.Message}");
                    _log.LogWarning("Run {RunId}: academic search {Source} failed: {Message}", runId, key, log.Error);
                }

                if (plan.StoppingRule == "saturation" && log.Error == null && log.New == 0) break;
            }
        }
    }

    /// <summary>The formal source as the pipeline's common record: authors as its producer, the venue as its site, the abstract as its summary.</summary>
    private static GreyRecord RecordOf(AcademicPaper paper, string address, string database) => new(
        paper.Id,
        paper.Title ?? "",
        paper.Abstract ?? "",
        address,
        string.Join(", ", (paper.Authors ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())),
        string.IsNullOrWhiteSpace(paper.JournalSource) ? database : paper.JournalSource.Trim(),
        FormalKind,
        DateOf(paper.PublishedDate),
        new Dictionary<string, long>());

    /// <summary>A full date when the database gives one; a year alone is kept only in the paper's record, never padded to a date.</summary>
    private static DateTime? DateOf(string? published) =>
        Regex.Match(published ?? "", @"\b(\d{4})-(\d{2})-(\d{2})\b") is { Success: true } m
        && DateTime.TryParseExact(m.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;

    private static RemovedRecord Removed(AcademicPaper paper, string database, string query, string reason, string? duplicateOf = null) =>
        new(paper.Id, paper.Title ?? "", database, query, ApaCitationBuilder.ExtractYear(paper.PublishedDate), reason, duplicateOf,
            PrismaReviewEngine.NormalizeDoi(paper.Doi));

    /// <summary>Every raw answer of one academic search, as received, with its SHA-256 (XML for arXiv, JSON for the others).</summary>
    private static async Task SaveFormalRawAsync(string rawFolder, string baseName, IReadOnlyList<string> raw, GreySearchLog log)
    {
        var names = new List<string>();
        var hashes = new List<string>();
        for (int r = 0; r < raw.Count; r++)
        {
            string name = baseName + (raw.Count > 1 ? $"-{r + 1}" : "") + (raw[r].TrimStart().StartsWith('<') ? ".xml" : ".json");
            byte[] bytes = new UTF8Encoding(false).GetBytes(raw[r]);
            await File.WriteAllBytesAsync(Path.Join(rawFolder, name), bytes);
            names.Add($"{RunArchive.RawResponsesFolder}/{name}");
            hashes.Add(RunManifest.Sha256(bytes));
        }
        log.RawFile = string.Join("; ", names);
        log.RawSha256 = string.Join("; ", hashes);
    }
}
