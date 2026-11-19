using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The version of TraceableAI a run was made with, and how to show and cite it. The version is set only by a git
/// tag vMAJOR.MINOR.PATCH (Semantic Versioning; MinVer reads it at build time) and the SDK adds the commit after a
/// plus sign: "0.2.0+40b00a0c…" for a build on the tag, "0.2.1-alpha.0.3+…" for a build three commits after it.
/// A version without a pre-release part is a published release with its own page on GitHub.
/// </summary>
public static class ToolVersion
{
    public const string RepositoryUrl = "https://github.com/lauPhilip/au-btech-literature-review-agent";

    private static readonly Regex Core = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Hex = new(@"^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>The version of the running build, as recorded in a run's files.</summary>
    public static string Current => RecordingChatCompletionService.AppVersion;

    /// <summary>The version without the commit: "0.2.0+40b00a0c…" → "0.2.0".</summary>
    public static string Number(string? version)
    {
        string v = (version ?? "").Trim();
        int plus = v.IndexOf('+');
        return plus < 0 ? v : v[..plus];
    }

    /// <summary>The full commit hash after the plus sign, or null when the build did not record one.</summary>
    public static string? Commit(string? version)
    {
        string v = (version ?? "").Trim();
        int plus = v.IndexOf('+');
        if (plus < 0) return null;
        // The SDK joins the commit to any build metadata already there with a dot, so the commit is the last part.
        string last = v[(plus + 1)..].Split('.').Last();
        return Hex.IsMatch(last) ? last.ToLowerInvariant() : null;
    }

    /// <summary>The commit shortened to seven characters, as GitHub shows it.</summary>
    public static string? ShortCommit(string? version) => Commit(version) is { } c ? c[..7] : null;

    /// <summary>Whether the version is a published release (MAJOR.MINOR.PATCH with no pre-release part).</summary>
    public static bool IsRelease(string? version) => Core.IsMatch(Number(version));

    /// <summary>The version for pages: "v0.2.0 (40b00a0)", or "v0.2.1-alpha.0.3 (40b00a0)" between releases.</summary>
    public static string Display(string? version)
    {
        string number = Number(version);
        if (number.Length == 0) return "unknown";
        string shown = char.IsDigit(number[0]) ? "v" + number : number;
        return ShortCommit(version) is { } c ? $"{shown} ({c})" : shown;
    }

    /// <summary>The page of the release on GitHub, or null for a build that is not a release.</summary>
    public static string? ReleaseUrl(string? version) => IsRelease(version) ? $"{RepositoryUrl}/releases/tag/v{Number(version)}" : null;

    /// <summary>Where the code of this version can be read: its release, else its commit, else the repository.</summary>
    public static string CodeUrl(string? version) =>
        ReleaseUrl(version) ?? (Commit(version) is { } c ? $"{RepositoryUrl}/tree/{c}" : RepositoryUrl);

    /// <summary>One sentence for a paper's availability statement, naming the version and where its code is.</summary>
    public static string PaperSentence(string? version)
    {
        string number = Number(version);
        if (number.Length == 0) return "";
        string? commit = ShortCommit(version);
        string which = commit == null ? $"version {number}" : $"version {number} (commit {commit})";
        if (IsRelease(version)) return $"This run used {which}, released at {ReleaseUrl(version)}.";
        return commit == null
            ? $"This run used {which}, which is not a published release."
            : $"This run used {which}, which is not a published release; its code is at {CodeUrl(version)}.";
    }
}
