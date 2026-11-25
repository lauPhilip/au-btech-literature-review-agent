using System.Globalization;

namespace AuBtechReviewAgent;

/// <summary>
/// The front page of a printed report (Components/Paper/ArticleFront.razor): the facts of the article-info box, the
/// abstract and the keywords, all taken from the run's files in code. It gives the printout a journal article's layout
/// but no journal's identity: no publisher, journal, volume, issue or page range, and no DOI, because a run has none
/// (the Zenodo DOI belongs to the tool, not to a paper it writes). The author is the tool, marked as an AI system.
/// Each kind of review builds one (<see cref="ForSystematic"/>, MultivocalArticleInfo.For).
/// </summary>
public sealed record ArticleInfo
{
    /// <summary>The author line. It names the tool, never a person, and the note says who is responsible.</summary>
    public const string Author = "TraceableAI – artificial researcher";
    public const string AuthorNote = "An AI system; the person who planned the review is responsible for its use.";
    public const string NotPeerReviewed = "A literature review written and checked by TraceableAI. It has not been peer reviewed.";

    public Guid RunId { get; init; }
    public string Title { get; init; } = "";
    public string Abstract { get; init; } = "";
    /// <summary>"Systematic review", "Multivocal literature review", ...</summary>
    public string Kind { get; init; } = "";
    /// <summary>The reporting guideline: "PRISMA 2020" or "Garousi et al. (2019)".</summary>
    public string Guideline { get; init; } = "";
    public DateTime? ProtocolWrittenUtc { get; init; }
    public DateTime? SearchedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    /// <summary>The version of TraceableAI that made the run, as <see cref="ToolVersion"/> reads it.</summary>
    public string? AppVersion { get; init; }
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    public int Found { get; init; }
    public string FoundLabel { get; init; } = "records found";
    public int Included { get; init; }
    public string IncludedLabel { get; init; } = "studies included";
    /// <summary>The SHA-256 fingerprint of protocol.md, or the short hash older runs recorded; empty when none.</summary>
    public string ProtocolFingerprint { get; init; } = "";
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    /// <summary>The report page, relative to the site (e.g. "spec-matrix/{id}").</summary>
    public string ReportPath { get; init; } = "";
    public bool HasProtocol { get; init; }

    public string ArchivePath => $"api/workspace/{RunId}/archive";
    public string ProtocolPath => $"api/workspace/{RunId}/protocol.md";

    /// <summary>The title for the running header: the part before a colon when it is long enough, cut at a word near 60 characters.</summary>
    public string ShortTitle
    {
        get
        {
            string title = Title.Trim();
            int colon = title.IndexOf(':');
            if (colon >= 12) title = title[..colon].Trim();
            if (title.Length <= 60) return title;
            int cut = title.LastIndexOf(' ', 59);
            return (cut > 20 ? title[..cut] : title[..59]).TrimEnd(',', ';', ' ') + "…";
        }
    }

    /// <summary>The run id's first eight characters, as the run is named on the site.</summary>
    public string ShortRunId => RunId.ToString("N")[..8];

    public static string Date(DateTime? utc) =>
        utc is DateTime d ? d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : "not recorded";

    public string ToolLine => ToolVersion.Display(AppVersion) is var shown && shown != "unknown" ? "TraceableAI " + shown : "TraceableAI (version not recorded)";

    /// <summary>A reference to this report in APA style, with the tool as author and the report's address.</summary>
    public string HowToCite(string baseUri)
    {
        string year = (CompletedUtc ?? SearchedUtc ?? ProtocolWrittenUtc)?.Year.ToString(CultureInfo.InvariantCulture) ?? "n.d.";
        string title = Title.Trim().TrimEnd('.');
        string version = ToolVersion.Number(AppVersion) is { Length: > 0 } v ? $" Version {v}." : "";
        return $"TraceableAI. ({year}). {title} [{Kind}, run {ShortRunId}; not peer reviewed].{version} {Absolute(baseUri, ReportPath)}";
    }

    /// <summary>
    /// The running header as CSS paged-media margin boxes: the short title on the left and the run on the right, on every
    /// page but the first. Chrome 131 and later print them; other browsers leave the header out. The title comes from the
    /// model, so every character but a letter, a digit or a space is written as a CSS escape: nothing in it can end the
    /// string, the rule or the style element.
    /// </summary>
    public string RunningHeaderCss() =>
        "@media print { @page { @top-left { content: " + CssString(ShortTitle) + "; } @top-right { content: " +
        CssString("TraceableAI · run " + ShortRunId) + "; } } @page :first { @top-left { content: none; } @top-right { content: none; } } }";

    public static string CssString(string text)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value == ' ')) sb.Append((char)rune.Value);
            else sb.Append('\\').Append(rune.Value.ToString("X", CultureInfo.InvariantCulture)).Append(' ');
        }
        return sb.Append('"').ToString();
    }

    /// <summary>An address on this site, absolute, so a printed or saved copy still leads back to it.</summary>
    public static string Absolute(string baseUri, string path) => baseUri.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>The systematic review's front page, from its ledger and report.</summary>
    public static ArticleInfo ForSystematic(Guid runId, ReviewState? state, PrismaReport report, string title, string @abstract, bool hasProtocol)
    {
        var models = new[] { state?.RunSettings?.Model, state?.RunSettings?.FastModel }
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        string fingerprint = state?.ProtocolSha256 is { Length: > 0 } sha ? sha : (report.ProtocolHash ?? "").Trim();
        return new ArticleInfo
        {
            RunId = runId,
            Title = title,
            Abstract = @abstract,
            Kind = "Systematic review",
            Guideline = "PRISMA 2020",
            ProtocolWrittenUtc = state?.Timestamp,
            SearchedUtc = FirstSearch(state),
            CompletedUtc = state?.CompletedUtc,
            AppVersion = state?.RunSettings?.AppVersion,
            Models = models,
            Found = state?.Stats.TotalIdentified ?? 0,
            FoundLabel = "records found",
            Included = state?.Stats.Included ?? 0,
            IncludedLabel = state?.Stats.Included == 1 ? "study included" : "studies included",
            ProtocolFingerprint = fingerprint == "not recorded" ? "" : fingerprint,
            Keywords = state == null ? Array.Empty<string>() : ArticleKeywords.From(state.SearchQuery,
                state.SearchPerspectives.Count > 0 ? state.SearchPerspectives : new List<string> { state.SearchQuery },
                state.ThematicSynthesis?.Themes.Select(t => t.Name)),
            ReportPath = $"spec-matrix/{runId}",
            HasProtocol = hasProtocol,
        };
    }

    // The search log's times are written as "yyyy-MM-dd HH:mm:ss UTC".
    private static DateTime? FirstSearch(ReviewState? state)
    {
        var times = (state?.SearchLogs ?? new()).Select(l => DateTime.TryParse(l.Timestamp.Replace(" UTC", "", StringComparison.Ordinal),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTime?)null)
            .Where(t => t != null).ToList();
        return times.Count > 0 ? times.Min() : null;
    }
}
