using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// One version of a review in the gallery: a run whose owner added it, with the figures its card and the versions list
/// show. Its files are in the entry's folder under v/{Number}/, so its address (/gallery/{entry}/v/{Number}) stays the
/// same while newer versions are added. Numbers are never reused, even after a version is dropped or withdrawn.
/// </summary>
public sealed record GalleryVersion
{
    public int Number { get; init; }
    public Guid RunId { get; init; }
    public string Title { get; init; } = "";
    public DateTime CompletedUtc { get; init; }

    /// <summary>When the owner added this run to the gallery.</summary>
    public DateTime AddedUtc { get; init; }

    public string ToolVersion { get; init; } = "";
    public int Included { get; init; }
    public int Identified { get; init; }
    public int Themes { get; init; }
    public int References { get; init; }
    public CitationShares Citations { get; init; } = new();
    public List<string> Keywords { get; init; } = new();

    /// <summary>The run's self-check of its paper (self-check.json), or null for a run made before it.</summary>
    public GallerySelfCheck? SelfCheck { get; init; }

    /// <summary>
    /// The DOI a repository (Zenodo, OSF) gave this version's deposit, as the maintainer wrote it in entry.json; null
    /// when there is none. It is read through <see cref="GalleryDoi.Normalize"/>, so a value that is not a DOI is ignored.
    /// </summary>
    public string? Doi { get; init; }

    /// <summary>SHA-256 of the edit key of this version's run, with a gallery prefix (GalleryFingerprint.OwnerToken).</summary>
    public string OwnerTokenSha256 { get; init; } = "";

    public string ShortRunId => RunId.ToString("N")[..8];

    /// <summary>The version a described run becomes when it is added.</summary>
    public static GalleryVersion From(GalleryEntry described, int number, DateTime addedUtc, string ownerToken) => new()
    {
        Number = number,
        RunId = described.RunId,
        Title = described.Title,
        CompletedUtc = described.CompletedUtc,
        AddedUtc = addedUtc,
        ToolVersion = described.ToolVersion,
        Included = described.Included,
        Identified = described.Identified,
        Themes = described.Themes,
        References = described.References,
        Citations = described.Citations,
        Keywords = described.Keywords.ToList(),
        SelfCheck = described.SelfCheck,
        OwnerTokenSha256 = ownerToken,
    };
}

/// <summary>How the citation check judged the report's citations, from the run's ledger (ReviewStats).</summary>
public sealed record CitationShares
{
    public int Checked { get; init; }
    public int Supported { get; init; }
    public int Partly { get; init; }
    public int NotSupported { get; init; }
    public int Unverifiable { get; init; }

    public static CitationShares Of(ReviewStats s) => new()
    {
        Checked = s.CitationsChecked,
        Supported = s.CitationsSupported,
        Partly = s.CitationsPartiallySupported,
        NotSupported = s.CitationsNotSupported,
        Unverifiable = s.CitationsUnverifiable,
    };

    /// <summary>A whole-number percentage of the citations checked; 0 when none were.</summary>
    public int Percent(int count) => Checked <= 0 ? 0 : (int)Math.Round(100.0 * count / Checked, MidpointRounding.AwayFromZero);

    /// <summary>True when the verdicts were recorded (entries added before this record only have the number checked).</summary>
    public bool HasVerdicts => Supported + Partly + NotSupported + Unverifiable > 0;
}

/// <summary>The counts of a run's self-check of its paper (Report/PaperSelfCheck.cs), as its self-check.json records them.</summary>
public sealed record GallerySelfCheck(int Passed, int Notes, int Faults)
{
    private static readonly System.Text.Json.JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>"8 checks passed, 2 notes, 1 fault".</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Summary => PaperSelfCheck.SummaryLine(Passed, Notes, Faults);

    /// <summary>
    /// The self-check the run wrote, read from its folder; null when the run has none. A run made before the self-check
    /// gets none here, so the gallery never shows a check the run did not make itself.
    /// </summary>
    public static GallerySelfCheck? Read(string folder)
    {
        string path = System.IO.Path.Join(folder, PaperSelfCheckFile.FileName);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var file = System.Text.Json.JsonSerializer.Deserialize<PaperSelfCheckFile>(System.IO.File.ReadAllText(path), ReadOptions);
            return file == null || file.Passed + file.Notes + file.Faults == 0 ? null : new(file.Passed, file.Notes, file.Faults);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
