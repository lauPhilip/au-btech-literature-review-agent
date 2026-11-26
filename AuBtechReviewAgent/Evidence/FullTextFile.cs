using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// Where a paper's PDF is kept in the run folder (#587). The name is a key of the paper's own: a hash of its DOI, or
/// of its record id when it has no DOI. Two papers whose titles start alike no longer share a file, and the same paper
/// gets the same name in every run, so a re-check after the run finds the copy the run used. What the file is
/// (title, id, DOI, address) is written beside it in a small JSON record, which goes into the archive with it. A copy
/// an older run saved under its title (<see cref="DocumentRAGUtility.PdfFileName"/>) is still read, never written.
/// </summary>
public static class FullTextFile
{
    /// <summary>The start of every full-text file name, so the archive can tell a PDF's record from the run's own JSON.</summary>
    public const string Prefix = "fulltext-";

    /// <summary>The record written beside a downloaded PDF.</summary>
    public sealed record Record(string Title, string Id, string? Doi, string? Url, string Key);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The key the paper's files are named by: its DOI when it has one, else its record id.</summary>
    public static string KeyOf(AcademicPaper paper) => KeyOf(paper.Id, paper.Doi);

    /// <summary>The key of a paper known by its record id and DOI (as the ledger holds them).</summary>
    public static string KeyOf(string id, string? doi) => NormalDoi(doi) is { } d ? "doi:" + d : "id:" + id.Trim();

    /// <summary>"fulltext-" and the first 20 hex digits of the key's SHA-256, then ".pdf".</summary>
    public static string PdfName(AcademicPaper paper) => PdfName(paper.Id, paper.Doi);

    /// <summary>The PDF name of a paper known by its record id and DOI.</summary>
    public static string PdfName(string id, string? doi) => Stem(KeyOf(id, doi)) + ".pdf";

    /// <summary>The JSON record beside the PDF.</summary>
    public static string RecordName(AcademicPaper paper) => RecordName(paper.Id, paper.Doi);

    /// <summary>The record name of a paper known by its record id and DOI.</summary>
    public static string RecordName(string id, string? doi) => Stem(KeyOf(id, doi)) + ".json";

    /// <summary>Whether a file in the run folder is a full-text record (and so belongs in the archive).</summary>
    public static bool IsRecordFile(string fileName) =>
        fileName.StartsWith(Prefix, StringComparison.Ordinal) && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes what the PDF in <paramref name="folder"/> is and where it came from.</summary>
    public static void WriteRecord(string folder, AcademicPaper paper, string? url)
    {
        var record = new Record(paper.Title ?? "", paper.Id, NormalDoi(paper.Doi), url, KeyOf(paper));
        File.WriteAllText(Path.Join(folder, RecordName(paper)), JsonSerializer.Serialize(record, Json));
    }

    private static string Stem(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Prefix + Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    /// <summary>A DOI without its resolver prefix, in lower case (DOIs are case-insensitive), or null.</summary>
    private static string? NormalDoi(string? doi)
    {
        if (string.IsNullOrWhiteSpace(doi)) return null;
        string d = doi.Trim();
        foreach (var prefix in new[] { "https://doi.org/", "http://doi.org/", "https://dx.doi.org/", "http://dx.doi.org/", "doi:" })
            if (d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) d = d[prefix.Length..];
        return d.Length == 0 ? null : d.ToLowerInvariant();
    }
}
