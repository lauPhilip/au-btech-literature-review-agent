using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Gallery" section of the configuration.</summary>
public sealed class GalleryOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folder of the gallery (relative paths are resolved against the content root), next to the metrics store.</summary>
    public string Folder { get; set; } = Path.Join("App_Data", "gallery");

    /// <summary>
    /// The most report versions the gallery keeps, counting every version of every review. Past it, the oldest version
    /// of the review with the most versions goes first; a review loses its last version only when no review has two.
    /// </summary>
    public int MaxEntries { get; set; } = 50;

    /// <summary>Entries an administrator has taken down: they are neither listed nor opened, and a new consent cannot bring them back.</summary>
    public List<string> HiddenIds { get; set; } = new();
}

/// <summary>
/// One review in the gallery: what the catalogue shows about it, the fingerprint that says which review it is, and its
/// versions. Each version is a copy of one run's report files in the entry's folder (v/{n}/), so it outlives the run
/// (which is deleted after Runs:RetentionDays) and is drawn by the same page as the run's own report. The entry's own
/// figures are those of the version it describes: the newest in entry.json, another one after ForVersion. Every value
/// here comes from the run's files and is shown as text.
/// </summary>
public sealed record GalleryEntry
{
    public const string FileName = "entry.json";

    /// <summary>The entry's own id in its address (/gallery/{Id}); 12 hex digits, unrelated to the run id.</summary>
    public string Id { get; init; } = "";
    public Guid RunId { get; init; }
    public string Module { get; init; } = "";
    public string Card { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> Questions { get; init; } = new();
    public DateTime CompletedUtc { get; init; }

    /// <summary>When the entry was first added; it stays when a newer run of the same review replaces the report.</summary>
    public DateTime AddedUtc { get; init; }

    /// <summary>When the newest version was added, if the review has more than one; null until then.</summary>
    public DateTime? UpdatedUtc { get; init; }

    /// <summary>The version of TraceableAI the run was made with, with its commit (ToolVersion).</summary>
    public string ToolVersion { get; init; } = "";
    public int Included { get; init; }
    public int Identified { get; init; }
    public int Themes { get; init; }
    public int CitationsChecked { get; init; }
    public int References { get; init; }
    public CitationShares Citations { get; init; } = new();
    public List<string> Keywords { get; init; } = new();

    /// <summary>The self-check of the version these figures describe; null when its run made none.</summary>
    public GallerySelfCheck? SelfCheck { get; init; }
    public string Fingerprint { get; init; } = "";

    /// <summary>SHA-256 of the run's edit key with a gallery prefix, in an entry made before versions; each version keeps its own.</summary>
    public string OwnerTokenSha256 { get; init; } = "";

    /// <summary>Every version kept, oldest first; empty only in an entry.json written before versions.</summary>
    public List<GalleryVersion> Versions { get; init; } = new();

    /// <summary>The highest version number ever given, so a number is never used twice.</summary>
    public int LastVersion { get; init; }

    /// <summary>The number of the version these figures describe.</summary>
    public int Version { get; init; }

    public GalleryVersion? Newest => Versions.Count == 0 ? null : Versions.MaxBy(v => v.Number);

    public GalleryVersion? VersionOf(int number) => Versions.FirstOrDefault(v => v.Number == number);

    /// <summary>The entry as one of its versions shows it: that run's title, date, figures and keywords.</summary>
    public GalleryEntry ForVersion(GalleryVersion v) => this with
    {
        RunId = v.RunId,
        Title = v.Title,
        CompletedUtc = v.CompletedUtc,
        ToolVersion = v.ToolVersion,
        Included = v.Included,
        Identified = v.Identified,
        Themes = v.Themes,
        References = v.References,
        Citations = v.Citations,
        CitationsChecked = v.Citations.Checked,
        Keywords = v.Keywords,
        SelfCheck = v.SelfCheck,
        Version = v.Number,
    };

    /// <summary>The first eight hex digits of the run id, as the Metrics page shows a run.</summary>
    public string ShortRunId => RunId.ToString("N")[..8];

    /// <summary>The date the catalogue sorts by: the last update, or when it was added.</summary>
    public DateTime LastChangedUtc => UpdatedUtc ?? AddedUtc;

    public bool IsValidId => GalleryFingerprint.IsEntryId(Id);
}

/// <summary>Which review a report is, so a new run of the same review updates its entry instead of adding a second one.</summary>
public static class GalleryFingerprint
{
    /// <summary>
    /// The same review is the same kind of review (its card) asking the same questions about the same topic. Case,
    /// punctuation and white space are folded, so "AI in Schools?" and "ai in  schools" are one review; any other word
    /// makes it a different one.
    /// </summary>
    public static string Of(string card, string topic, IEnumerable<string> questions)
    {
        string text = Fold(card) + "\n" + Fold(topic) + "\n" + string.Join("\n", questions.Select(Fold).Where(q => q.Length > 0));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    }

    /// <summary>Lower case, letters and digits only, one space between words.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The token kept for withdrawing: never the edit key itself, nor the hash in the run's owner.json.</summary>
    public static string OwnerToken(string editKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("gallery|" + editKey))).ToLowerInvariant();

    public static string NewEntryId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>True for 12 lower-case hex digits, the only form an entry id has, so an id can never name another folder.</summary>
    public static bool IsEntryId(string? id) =>
        id is { Length: 12 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
