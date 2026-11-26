using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>One quote a public copy shortened, with a pointer to the full quote in the owner's archive.</summary>
/// <param name="File">The file the quote is in, e.g. extraction.json.</param>
/// <param name="Path">Where in that file, e.g. $[2].KeyFindings[0].Quote.</param>
/// <param name="Source">The source it quotes: "reference 4", or "paper {id}" where no reference number is given.</param>
/// <param name="Words">Words in the full quote.</param>
/// <param name="KeptWords">Words the public copy keeps.</param>
/// <param name="Sha256">SHA-256 of the full quote (UTF-8), to find and check it in the owner's archive.</param>
public sealed record ShortenedQuote(string File, string Path, string Source, int Words, int KeptWords, string Sha256);

/// <summary>What a public copy changed, written next to it as public-copy.json.</summary>
public sealed class PublicCopyRecord
{
    public string Rule { get; set; } = PublicCopy.RuleText;
    public int AbstractsRemoved { get; set; }
    public int AbstractsKept { get; set; }
    public int QuotesShortened { get; set; }
    public List<ShortenedQuote> Shortened { get; set; } = new();
}

/// <summary>
/// The rules for a copy of a run that anyone can read: the gallery's copy and the deposit archive for Zenodo or OSF.
/// The owner's own archive keeps every file as it is. Publishers' terms decide what a public copy may hold: the
/// Scopus API terms forbid showing Scopus data on a public website, and the STM permission guidelines (up to 400 words
/// from a work) cover publications, not public platforms. So a public copy leaves out the abstract of every screened
/// record unless it is arXiv's (arXiv gives its metadata, abstracts included, under CC0), shortens each quote to
/// <see cref="MaxQuoteWords"/> words and quotes at most <see cref="MaxWordsPerSource"/> words of one source in all.
/// A shortened quote ends with an ellipsis and <see cref="ShortenedNote"/>, so every page that shows it says so, and
/// the record keeps the SHA-256 of the full quote. The same quote in two files is shortened the same way and counted once.
/// </summary>
public sealed class PublicCopy
{
    public const string RecordFile = "public-copy.json";
    public const int MaxQuoteWords = 50;
    public const int MaxWordsPerSource = 300;
    public const string Ellipsis = "…";
    public const string ShortenedNote = "[shortened in this public copy; the full quote is in the owner's run archive]";

    public const string RuleText =
        "Abstracts of screened records are left out unless they come from arXiv (CC0 metadata). Each quote keeps at most " +
        "50 words, and at most 300 words are quoted from one source in all; a shortened quote ends with an ellipsis and a note. " +
        "The owner's run archive holds every abstract and the full quotes; Sha256 identifies each full quote there.";

    private static readonly string[] QuoteProperties = { "Quote", "SecondQuote" };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly Dictionary<string, int> _usedWords = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Quote), string> _decided = new();

    public PublicCopyRecord Record { get; } = new();

    /// <summary>
    /// The public form of one JSON file, or the same text when nothing in it had to change. Null when the file is not
    /// valid JSON: a public copy then leaves it out rather than publish what could not be checked.
    /// </summary>
    public string? Redact(string fileName, string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root == null) return json;
        bool changed = Walk(root, fileName, "$", null);
        return changed ? root.ToJsonString(Indented) : json;
    }

    /// <summary>
    /// Makes a folder's JSON files public in place, in name order so the word budget always falls the same way, and
    /// writes <see cref="RecordFile"/>. A file that is not valid JSON is deleted. <paramref name="skip"/> names files
    /// that hold no source text (the gallery's entry.json).
    /// </summary>
    public static PublicCopyRecord ApplyToFolder(string folder, params string[] skip)
    {
        var copy = new PublicCopy();
        var files = Directory.GetFiles(folder, "*.json")
            .Where(f => !string.Equals(Path.GetFileName(f), RecordFile, StringComparison.OrdinalIgnoreCase)
                        && !skip.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);
        foreach (var path in files)
        {
            string text = File.ReadAllText(path);
            string? redacted = copy.Redact(Path.GetFileName(path), text);
            if (redacted == null) File.Delete(path);
            else if (!ReferenceEquals(redacted, text)) File.WriteAllText(path, redacted, new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Join(folder, RecordFile), copy.RecordJson(), new UTF8Encoding(false));
        return copy.Record;
    }

    public string RecordJson() => JsonSerializer.Serialize(Record, Indented);

    /// <summary>True for an abstract a public copy may keep: an arXiv record's (by its id or its arXiv DOI).</summary>
    public static bool IsOpenAbstract(JsonObject record)
    {
        string id = Text(record, "PaperId") ?? Text(record, "Id") ?? "";
        string doi = Text(record, "Doi") ?? "";
        return id.Contains("arxiv.org/", StringComparison.OrdinalIgnoreCase)
               || doi.StartsWith("10.48550/arxiv.", StringComparison.OrdinalIgnoreCase);
    }

    private bool Walk(JsonNode node, string file, string path, string? source)
    {
        bool changed = false;
        if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
                if (array[i] is { } item) changed |= Walk(item, file, $"{path}[{i}]", source);
            return changed;
        }
        if (node is not JsonObject obj) return false;

        source = SourceOf(obj) ?? source;
        foreach (var (name, value) in obj.ToList())
        {
            if (value == null) continue;
            string here = $"{path}.{name}";
            if (name.Equals("Abstract", StringComparison.OrdinalIgnoreCase) && IsText(value, out string abstractText) && abstractText.Trim().Length > 0)
            {
                if (IsOpenAbstract(obj)) Record.AbstractsKept++;
                else { obj[name] = ""; Record.AbstractsRemoved++; changed = true; }
            }
            else if (QuoteProperties.Contains(name, StringComparer.OrdinalIgnoreCase) && IsText(value, out string quote))
            {
                string shortened = Shorten(source ?? "unknown source", quote, file, here);
                if (!ReferenceEquals(shortened, quote)) { obj[name] = shortened; changed = true; }
            }
            else changed |= Walk(value, file, here, source);
        }
        return changed;
    }

    /// <summary>The quote as the public copy shows it; the same string when it fits within both limits.</summary>
    public string Shorten(string source, string quote, string file, string path)
    {
        var words = Regex.Split(quote.Trim(), @"\s+", RegexOptions.None, TimeSpan.FromSeconds(1)).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return quote;
        if (!_decided.TryGetValue((source, quote), out var result))
        {
            int used = _usedWords.GetValueOrDefault(source);
            int allowed = Math.Max(0, Math.Min(MaxQuoteWords, MaxWordsPerSource - used));
            int kept = Math.Min(words.Length, allowed);
            _usedWords[source] = used + kept;
            result = kept == words.Length ? quote
                : (kept == 0 ? Ellipsis : string.Join(" ", words.Take(kept)) + " " + Ellipsis) + " " + ShortenedNote;
            _decided[(source, quote)] = result;
        }
        if (ReferenceEquals(result, quote)) return quote;
        Record.QuotesShortened++;
        Record.Shortened.Add(new ShortenedQuote(file, path, source, words.Length, KeptWordsOf(result), Sha256(quote)));
        return result;
    }

    private static int KeptWordsOf(string shortened)
    {
        int cut = shortened.LastIndexOf(" " + ShortenedNote, StringComparison.Ordinal);
        string kept = (cut >= 0 ? shortened[..cut] : shortened).Trim();
        if (kept.EndsWith(Ellipsis, StringComparison.Ordinal)) kept = kept[..^Ellipsis.Length].Trim();
        return kept.Length == 0 ? 0 : kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // The source a quote belongs to: the reference number where the record has one (extraction, codebook, citation
    // audit), else the paper id. Both name the same study within one run.
    private static string? SourceOf(JsonObject obj)
    {
        foreach (var name in new[] { "ReferenceNumber", "Reference" })
            if (obj.TryGetPropertyValue(name, out var n) && n is JsonValue v && v.TryGetValue(out int number) && number > 0)
                return $"reference {number}";
        return Text(obj, "PaperId") is { Length: > 0 } id ? $"paper {id}" : null;
    }

    private static string? Text(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool IsText(JsonNode node, out string text)
    {
        text = node is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String;
    }

    public static string Sha256(string text) => RunManifest.Sha256(new UTF8Encoding(false).GetBytes(text));
}
