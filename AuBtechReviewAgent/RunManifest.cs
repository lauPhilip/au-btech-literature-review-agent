using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// manifest.json in every download: the SHA-256 fingerprint and size of every file in the archive, plus the
/// run id, protocol hash and exact app version. Anyone can recompute the fingerprints (sha256sum, or
/// Get-FileHash in PowerShell) and see whether any file was changed after the run. The manifest also carries
/// a fingerprint of itself without that field, so it cannot be edited unnoticed either.
/// </summary>
public static class RunManifest
{
    public record Entry(string Path, long Bytes, string Sha256);

    public static string Build(Guid runId, ReviewState? state, IReadOnlyList<(string Path, byte[] Content)> files)
    {
        var entries = files
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new Entry(f.Path, f.Content.LongLength, Sha256(f.Content)))
            .ToList();

        var body = new Dictionary<string, object?>
        {
            ["RunId"] = runId.ToString(),
            ["ProtocolHash"] = state?.ProtocolHash,
            ["AppVersion"] = RecordingChatCompletionService.AppVersion,
            ["Model"] = state?.RunSettings?.Model,
            ["CreatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["HashAlgorithm"] = "SHA-256",
            ["Files"] = entries,
        };
        string withoutSelf = JsonSerializer.Serialize(body);
        body["ManifestSha256"] = Sha256(Encoding.UTF8.GetBytes(withoutSelf));
        return JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Recomputes every fingerprint against the given files; returns the paths that do not match.</summary>
    public static IReadOnlyList<string> Verify(string manifestJson, IReadOnlyDictionary<string, byte[]> files)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var mismatches = new List<string>();
        foreach (var e in doc.RootElement.GetProperty("Files").EnumerateArray())
        {
            string path = e.GetProperty("Path").GetString()!;
            string expected = e.GetProperty("Sha256").GetString()!;
            if (!files.TryGetValue(path, out var content) || Sha256(content) != expected) mismatches.Add(path);
        }
        return mismatches;
    }

    public static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>
/// protocol.md: the review plan written before the search starts (query, objective, criteria, sources,
/// limits, screening setup, model). PRISMA 2020 asks where a protocol can be accessed; this is it, with a
/// timestamp and fingerprint that predate every result. The search strings the model adds are appended
/// afterwards as a clearly dated amendment.
/// </summary>
public static class ProtocolWriter
{
    public static string Write(Guid runId, ReviewRequest request, IReadOnlyList<string> sourceNames, string model, RunsOptions runs, DateTime createdUtc, IReadOnlyList<string>? inputFlags = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Review protocol");
        sb.AppendLine();
        sb.AppendLine($"Run {runId} — written {createdUtc:yyyy-MM-dd HH:mm:ss} UTC, before any search was run.");
        sb.AppendLine();
        sb.AppendLine("## Question");
        sb.AppendLine();
        sb.AppendLine($"- Search query: \"{request.Query}\"");
        sb.AppendLine($"- Review objective: {request.Objective}");
        sb.AppendLine();
        sb.AppendLine("## Eligibility");
        sb.AppendLine();
        sb.AppendLine($"- Inclusion: {request.Inclusion}");
        sb.AppendLine($"- Exclusion: {request.Exclusion}");
        sb.AppendLine($"- Peer-reviewed only: {(request.PeerReviewOnly ? "yes" : "no")}");
        sb.AppendLine($"- Publication years: {(request.YearFrom > 0 && request.YearTo > 0 ? $"{request.YearFrom}–{request.YearTo}" : "no limit")}");
        sb.AppendLine();
        sb.AppendLine("## Information sources and search");
        sb.AppendLine();
        sb.AppendLine($"- Sources: {string.Join(", ", sourceNames)}");
        sb.AppendLine($"- At most {request.MaxResultsPerSource} records per source; the language model adds up to three extra search strings (recorded below as an amendment).");
        sb.AppendLine($"- Citation chaining (references and citing papers of included studies via OpenAlex): {(request.CitationChaining ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("## Selection, extraction and appraisal");
        sb.AppendLine();
        sb.AppendLine($"- Screening by {model} at temperature 0 on title, abstract and metadata{(request.DualScreening ? ", done twice with two independent prompts; disagreements are included and flagged for human attention" : "")}.");
        sb.AppendLine($"- Human check of screening decisions before the write-up: {(request.HumanScreeningReview ? $"yes (waits up to {runs.ScreeningReviewTimeoutHours} hours)" : "no")}");
        sb.AppendLine("- Data extraction (study type, method, sample, findings, limitations) and quality appraisal with MMAT 2018 for each included study, every item backed by a verified quote.");
        sb.AppendLine("- Citation support check of every cited sentence in the synthesis.");
        string artifactKind = ArtifactKinds.Normalize(request.ArtifactKind);
        sb.AppendLine(artifactKind == ArtifactKinds.None
            ? "- No artifact was requested."
            : $"- Artifact built from the results: {ArtifactKinds.All.First(k => k.Key == artifactKind).Label}{(string.IsNullOrWhiteSpace(request.SynthesisDirective) ? "" : $" (\"{request.SynthesisDirective}\")")}.");
        if (inputFlags is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Input check");
            sb.AppendLine();
            sb.AppendLine("The query, objective or criteria above contain phrases that read like instructions to a language model. The person running the review saw this warning and started the run anyway:");
            sb.AppendLine();
            foreach (var flag in inputFlags) sb.AppendLine($"- {flag}");
        }
        return sb.ToString();
    }

    public static string Amendment(IReadOnlyList<string> searchStrings, DateTime utc)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## Amendment {utc:yyyy-MM-dd HH:mm:ss} UTC: search strings used");
        sb.AppendLine();
        foreach (var s in searchStrings) sb.AppendLine($"- \"{s}\"");
        return sb.ToString();
    }
}
