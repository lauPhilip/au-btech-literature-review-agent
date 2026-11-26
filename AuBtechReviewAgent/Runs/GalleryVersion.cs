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
