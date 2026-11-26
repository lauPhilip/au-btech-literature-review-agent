using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>A file of the owner's archive that the deposit leaves out, why, and its SHA-256 in the owner's manifest.</summary>
public sealed record DepositLeftOut(string Path, string Reason, string? Sha256);

/// <summary>A file the deposit holds in its public form, with its SHA-256 in the owner's archive and in the deposit.</summary>
public sealed record DepositChanged(string Path, string OwnerSha256, string DepositSha256);

/// <summary>deposit.json: what the deposit archive holds compared with the owner's archive, and why.</summary>
public sealed class DepositRecord
{
    public string Rule { get; set; } = DepositArchive.RuleText;
    public string? RunId { get; set; }
    public string? OwnerManifestSha256 { get; set; }
    public Dictionary<string, string> PapersKept { get; set; } = new();
    public List<DepositLeftOut> LeftOut { get; set; } = new();
    public List<DepositChanged> Changed { get; set; } = new();
    public PublicCopyRecord PublicCopy { get; set; } = new();
}

/// <summary>
/// The deposit archive: a copy of the owner's run archive that may be put in a public repository such as Zenodo or
/// OSF. It is made from the owner's zip, so it works for every kind of review and needs nothing the archive does not
/// hold. It leaves out the downloaded papers without an open licence on file (<see cref="DepositLicences"/>), the raw
/// answers of licensed databases, the model call log (its prompts hold the papers' text and the databases' abstracts)
/// and any sub-folder it does not know; its JSON files follow the public-copy rules (<see cref="PublicCopy"/>). Every
/// left-out or changed file is listed in deposit.json with its SHA-256 in the owner's archive, so the two can be matched,
/// and the deposit has its own manifest.json. The owner's archive itself is not changed.
/// </summary>
public static class DepositArchive
{
    public const string RecordFile = "deposit.json";
    public const string ReadmeFile = "DEPOSIT-README.md";
    private const long MaxJsonBytes = 64L * 1024 * 1024;

    public const string RuleText =
        "A copy of the run archive for a public repository. Downloaded papers are kept only when OpenAlex or Unpaywall " +
        "recorded a Creative Commons or public-domain licence for them; raw answers are kept only from open services " +
        "(OpenAlex, arXiv, Crossref, Unpaywall); llm-calls.json is left out because its prompts hold the papers' text; " +
        "the JSON files follow the public-copy rules in PublicCopy.Rule. The owner's run archive holds everything.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Writes the deposit made from <paramref name="ownerArchive"/> (a seekable zip) to <paramref name="output"/>.</summary>
    public static DepositRecord Write(Stream ownerArchive, Stream output)
    {
        using var owner = new ZipArchive(ownerArchive, ZipArchiveMode.Read, leaveOpen: true);
        var entries = owner.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        var manifest = ReadManifest(entries.FirstOrDefault(e => e.FullName == "manifest.json"));
        var record = new DepositRecord { RunId = manifest.RunId, OwnerManifestSha256 = manifest.Self };

        var licences = new DepositLicences();
        foreach (var e in entries.Where(e => IsIn(e, RunArchive.RawResponsesFolder) && DepositLicences.IsOpenAnswer(e.Name) && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            if (ReadText(e) is { } json) licences.ReadAnswer(e.Name, json);
        if (entries.FirstOrDefault(e => e.FullName == GallerySnapshot.LedgerFile) is { } ledger && ReadText(ledger) is { } ledgerJson)
            licences.ReadLedger(ledgerJson);

        // The JSON files in name order, so the public-copy word budget always falls the same way.
        var copy = new PublicCopy();
        var publicJson = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries.Where(IsRootJson).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            string? text = ReadText(e);
            string? redacted = text == null ? null : copy.Redact(e.FullName, text);
            if (redacted == null) invalid.Add(e.FullName);
            else if (!ReferenceEquals(redacted, text)) publicJson[e.FullName] = new UTF8Encoding(false).GetBytes(redacted);
        }
        record.PublicCopy = copy.Record;

        var fingerprints = new List<RunManifest.Entry>();
        using (var deposit = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                string name = e.FullName;
                if (name == "manifest.json") continue;
                string? reason = invalid.Contains(name) ? "not valid JSON, or too large, so it could not be checked" : LeaveOutReason(e, licences);
                if (reason != null) { record.LeftOut.Add(new(name, reason, manifest.Sha(name))); continue; }
                if (publicJson.TryGetValue(name, out var bytes))
                {
                    var entry = Add(deposit, name, bytes);
                    fingerprints.Add(entry);
                    record.Changed.Add(new(name, manifest.Sha(name) ?? "", entry.Sha256));
                }
                else fingerprints.Add(CopyEntry(deposit, e));
                if (IsIn(e, RunArchive.SourceTextsFolder)) record.PapersKept[name] = licences.OpenPapers[e.Name];
            }
            fingerprints.Add(Add(deposit, RecordFile, new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(record, Indented))));
            fingerprints.Add(Add(deposit, ReadmeFile, new UTF8Encoding(false).GetBytes(Readme(record))));
            Add(deposit, "manifest.json", new UTF8Encoding(false).GetBytes(RunManifest.Build(
                Guid.TryParse(manifest.RunId, out var runId) ? runId : Guid.Empty, manifest.ProtocolHash, manifest.Model, fingerprints)));
        }
        return record;
    }

    /// <summary>Why the deposit leaves this file of the owner's archive out; null when it keeps it.</summary>
    private static string? LeaveOutReason(ZipArchiveEntry e, DepositLicences licences)
    {
        if (e.FullName == "llm-calls.json") return "the model call log: its prompts hold the papers' text and the databases' abstracts";
        if (IsIn(e, RunArchive.SourceTextsFolder))
            return licences.OpenPapers.ContainsKey(e.Name) ? null : "a downloaded paper with no open licence on file from OpenAlex or Unpaywall";
        if (IsIn(e, RunArchive.RawResponsesFolder))
            return DepositLicences.IsOpenAnswer(e.Name) ? null : "a raw answer of a licensed database, or of a service whose terms are not known";
        if (e.FullName.Contains('/')) return "a folder the deposit does not know";
        return null;
    }

    private static string Readme(DepositRecord record) => string.Join("\n", new[]
    {
        "# Deposit copy of a TraceableAI run archive",
        "",
        $"This archive is the deposit copy of run {record.RunId ?? "unknown"}, made to be shared in a public repository such as Zenodo or OSF.",
        "It holds the protocol, the ledger, the extraction, the citation audit, the manuscript and the references of the run.",
        $"It leaves out {record.LeftOut.Count} files of the owner's run archive and holds {record.Changed.Count} files in a shortened public form.",
        $"{record.PapersKept.Count} downloaded papers are included because an open licence is on file for them.",
        "",
        "Publishers' terms do not allow their abstracts, full texts and database records to be shared on a public platform, so this copy does not hold them.",
        "Quotes keep at most 50 words each and 300 words of one source in all; a shortened quote ends with an ellipsis and a note.",
        "",
        "deposit.json lists every file left out or changed, with its SHA-256 in the owner's archive, so this copy can be matched with the full archive the review's owner keeps.",
        "manifest.json holds the SHA-256 of every file in this copy.",
        "",
    });

    private static bool IsIn(ZipArchiveEntry e, string folder) => e.FullName.StartsWith(folder + "/", StringComparison.Ordinal);

    private static bool IsRootJson(ZipArchiveEntry e) =>
        !e.FullName.Contains('/') && e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && e.FullName is not ("manifest.json" or "llm-calls.json");

    private static string? ReadText(ZipArchiveEntry e)
    {
        if (e.Length > MaxJsonBytes) return null;
        using var reader = new StreamReader(e.Open(), new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    private static RunManifest.Entry Add(ZipArchive deposit, string name, byte[] content)
    {
        using (var target = deposit.CreateEntry(name, CompressionLevel.Optimal).Open()) target.Write(content, 0, content.Length);
        return new RunManifest.Entry(name, content.LongLength, RunManifest.Sha256(content));
    }

    // Copies one file in pieces and fingerprints it as it goes, so a large PDF is never held in memory.
    private static RunManifest.Entry CopyEntry(ZipArchive deposit, ZipArchiveEntry e)
    {
        using var source = e.Open();
        using var target = deposit.CreateEntry(e.FullName, CompressionLevel.Optimal).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            target.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        return new RunManifest.Entry(e.FullName, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private sealed record OwnerManifest(string? RunId, string? ProtocolHash, string? Model, string? Self, Dictionary<string, string> Files)
    {
        public string? Sha(string path) => Files.TryGetValue(path, out var sha) ? sha : null;
    }

    private static OwnerManifest ReadManifest(ZipArchiveEntry? entry)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry == null || ReadText(entry) is not { } json) return new(null, null, null, null, files);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.TryGetProperty("Files", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var f in list.EnumerateArray())
                    if (f.TryGetProperty("Path", out var p) && f.TryGetProperty("Sha256", out var s) && p.GetString() is { } path && s.GetString() is { } sha)
                        files[path] = sha;
            return new(Str("RunId"), Str("ProtocolHash"), Str("Model"), Str("ManifestSha256"), files);
        }
        catch (JsonException) { return new(null, null, null, null, files); }
    }
}
