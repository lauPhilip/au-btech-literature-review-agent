using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>One editorial notice on an included study, as Crossref lists it.</summary>
public sealed class RetractionNotice
{
    /// <summary>Crossref's update type, such as "retraction", "correction" or "expression_of_concern".</summary>
    public string Type { get; set; } = "";
    /// <summary>The DOI of the notice (for a Retraction Watch entry, the notice Retraction Watch recorded).</summary>
    public string NoticeDoi { get; set; } = "";
    /// <summary>"publisher" or "retraction-watch".</summary>
    public string Source { get; set; } = "";
    /// <summary>The date of the update as yyyy-MM-dd, or "" when Crossref gives none.</summary>
    public string Date { get; set; } = "";
}

/// <summary>What the check found for one included study with a Crossref DOI.</summary>
public sealed class RetractionCheckEntry
{
    public string PaperId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Doi { get; set; } = "";
    public List<ReferenceLookupLog> Lookups { get; set; } = new();
    public List<RetractionNotice> Notices { get; set; } = new();
    /// <summary>One of the <see cref="RetractionStatus"/> values.</summary>
    public string Status { get; set; } = RetractionStatus.None;
    /// <summary>Why the check could not be completed, when <see cref="Status"/> is "not checked".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotCheckedReason { get; set; }
    /// <summary>
    /// True when Crossref has no record of the DOI (answer "not found"): a DOI registered elsewhere, such as DataCite's
    /// Zenodo DOIs. The study then has no DOI registered at Crossref; it is not a check that failed.
    /// </summary>
    [JsonIgnore]
    public bool NotAtCrossref => Status == RetractionStatus.NotChecked && Lookups.Count > 0 && Lookups[0].Status == "not found";

    /// <summary>For a preprint: the DOI of the published version that Crossref names (relation is-preprint-of).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublishedVersionDoi { get; set; }
}

/// <summary>The outcome of the check for one study, worst first.</summary>
public static class RetractionStatus
{
    public const string Retracted = "retracted";
    public const string Concern = "expression of concern";
    public const string Corrected = "corrected";
    public const string None = "none";
    public const string NotChecked = "not checked";
}

/// <summary>retraction-check.json: every included study with a Crossref DOI, what was asked and what was found.</summary>
public sealed class RetractionCheckFile
{
    public const string FileName = "retraction-check.json";
    /// <summary>The included studies when the check ran, those with a Crossref DOI or not.</summary>
    public int IncludedAtCheck { get; set; }
    public List<RetractionCheckEntry> Studies { get; set; } = new();
}

/// <summary>
/// Checks each included study's DOI for retractions, corrections and expressions of concern before its full text is
/// read. Crossref carries the publishers' notices and the Retraction Watch database; a notice lists the work it updates in
/// its "update-to" field, and the documented filter "updates:{DOI}" finds every notice that updates a DOI. The study's own
/// Crossref record (the one the reference enrichment uses, so it is fetched once) must name the same title before any
/// notice counts, so a DOI a database gave to the wrong record cannot exclude a study; it also names a preprint's published
/// version (relation "is-preprint-of"). No model takes part, and every answer is saved like the enrichment's.
/// </summary>
public static class RetractionCheck
{
    private static readonly string[] RetractionTypes = { "retraction", "withdrawal", "removal" };
    private static readonly string[] ConcernTypes = { "expression_of_concern" };
    private static readonly string[] CorrectionTypes = { "correction", "erratum", "corrigendum", "partial_retraction", "addendum", "clarification" };

    public static async Task<RetractionCheckFile> CheckAsync(IReadOnlyList<ScreeningLog> included, string runFolder,
        Func<string, Task<string>> get, Func<string, string> withMailto)
    {
        var file = new RetractionCheckFile { IncludedAtCheck = included.Count };
        foreach (var log in included)
        {
            // A DataCite DOI (arXiv, Zenodo) is not at Crossref.
            if (ReferenceEnricher.BareDoi(log.Doi) is not { } doi || doi.StartsWith("10.48550/", StringComparison.OrdinalIgnoreCase)) continue;
            file.Studies.Add(await CheckOneAsync(log, doi, runFolder, get, withMailto));
        }
        return file;
    }

    private static async Task<RetractionCheckEntry> CheckOneAsync(ScreeningLog log, string doi, string runFolder,
        Func<string, Task<string>> get, Func<string, string> withMailto)
    {
        var entry = new RetractionCheckEntry { PaperId = log.PaperId, Title = log.Title, Doi = doi };
        string escaped = ReferenceEnricher.EscapeDoi(doi);
        var work = new ReferenceLookupLog { Service = ReferenceAnswers.Crossref, Url = $"https://api.crossref.org/works/{escaped}" };
        var updates = new ReferenceLookupLog
        {
            Service = ReferenceAnswers.Crossref,
            Url = $"https://api.crossref.org/works?filter=updates:{Uri.EscapeDataString(doi)}&select=DOI,update-to&rows=20",
        };
        entry.Lookups.Add(work);
        try
        {
            // The same name as the enrichment's answer, so the record is fetched once per run.
            string? record = await ReferenceEnricher.FetchSavedAsync(get, runFolder, "reference", work, withMailto);
            string? title = record == null ? null : ReferenceAnswers.FromCrossref(record)?.Title;
            if (record == null || title == null || !ReferenceAnswers.SameTitle(title, log.Title))
                return NotChecked(entry, title == null ? $"Crossref's record of the DOI could not be read ({work.Status})"
                    : $"Crossref's record of the DOI names another title (\"{title}\")");
            entry.Lookups.Add(updates);
            string? notices = await ReferenceEnricher.FetchSavedAsync(get, runFolder, "retraction", updates, withMailto);
            if (notices == null) return NotChecked(entry, $"the notices could not be fetched ({updates.Status})");
            entry.PublishedVersionDoi = PublishedVersionOf(record);
            entry.Notices = NoticesFor(notices, doi);
        }
        catch (JsonException)
        {
            return NotChecked(entry, "an answer could not be read");
        }
        entry.Status = StatusOf(entry.Notices);
        return entry;
    }

    private static RetractionCheckEntry NotChecked(RetractionCheckEntry entry, string reason)
    {
        entry.Status = RetractionStatus.NotChecked;
        entry.NotCheckedReason = reason;
        return entry;
    }

    /// <summary>The worst notice: a retraction before an expression of concern before a correction.</summary>
    public static string StatusOf(IReadOnlyList<RetractionNotice> notices) =>
        notices.Any(n => RetractionTypes.Contains(n.Type)) ? RetractionStatus.Retracted
        : notices.Any(n => ConcernTypes.Contains(n.Type)) ? RetractionStatus.Concern
        : notices.Any(n => CorrectionTypes.Contains(n.Type)) ? RetractionStatus.Corrected
        : RetractionStatus.None;

    /// <summary>
    /// The notices in an answer to works?filter=updates:{DOI} that update <paramref name="doi"/>: one per type, notice and
    /// source, of the types this check knows. Pure, so a run's saved answer can be read again the same way.
    /// </summary>
    public static List<RetractionNotice> NoticesFor(string json, string doi)
    {
        using var answer = JsonDocument.Parse(json);
        var found = new List<RetractionNotice>();
        if (!answer.RootElement.TryGetProperty("message", out var m) || !m.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return found;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("update-to", out var updates) || updates.ValueKind != JsonValueKind.Array) continue;
            foreach (var u in updates.EnumerateArray())
            {
                if (!Str(u, "DOI").Equals(doi, StringComparison.OrdinalIgnoreCase)) continue;
                string type = Str(u, "type").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                if (!RetractionTypes.Concat(ConcernTypes).Concat(CorrectionTypes).Contains(type)) continue;
                var notice = new RetractionNotice { Type = type, NoticeDoi = Str(item, "DOI"), Source = Str(u, "source"), Date = DateOf(u) };
                if (!found.Any(f => f.Type == notice.Type && f.Source == notice.Source && f.NoticeDoi.Equals(notice.NoticeDoi, StringComparison.OrdinalIgnoreCase)))
                    found.Add(notice);
            }
        }
        return found;
    }

    /// <summary>The DOI of the published version a posted preprint names in its Crossref record, or null.</summary>
    public static string? PublishedVersionOf(string json)
    {
        using var answer = JsonDocument.Parse(json);
        if (!answer.RootElement.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
            || !m.TryGetProperty("relation", out var relation) || relation.ValueKind != JsonValueKind.Object
            || !relation.TryGetProperty("is-preprint-of", out var of) || of.ValueKind != JsonValueKind.Array) return null;
        return of.EnumerateArray()
            .Where(r => Str(r, "id-type").Equals("doi", StringComparison.OrdinalIgnoreCase))
            .Select(r => ReferenceEnricher.BareDoi(Str(r, "id")))
            .FirstOrDefault(d => d != null);
    }

    private static string DateOf(JsonElement update)
    {
        if (!update.TryGetProperty("updated", out var u) || !u.TryGetProperty("date-parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0 || parts[0].ValueKind != JsonValueKind.Array) return "";
        var p = parts[0].EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList();
        return p.Count switch
        {
            >= 3 => $"{p[0]:D4}-{p[1]:D2}-{p[2]:D2}",
            2 => $"{p[0]:D4}-{p[1]:D2}",
            1 => $"{p[0]:D4}",
            _ => "",
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : ""
            : "";
}
