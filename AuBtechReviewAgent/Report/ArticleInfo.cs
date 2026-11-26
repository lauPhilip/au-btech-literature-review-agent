using System.Globalization;

namespace AuBtechReviewAgent;

/// <summary>
/// The front page of a report, on screen and on paper (Components/Paper/ArticleFront.razor): the masthead, the facts of
/// the article-info box, the abstract and the keywords, all taken from the run's files in code, with no model call. It gives the printout a journal article's layout
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
    /// <summary>The banner under the masthead.</summary>
    public const string Disclosure = "Preprint written by an AI system. Not peer reviewed. Every claim links to a verbatim quote in the source archive.";
    /// <summary>What a reader learns about the review's registration; no run is registered.</summary>
    public const string Registration = "Not registered";

    public Guid RunId { get; init; }
    public string Title { get; init; } = "";
    public string Abstract { get; init; } = "";
    /// <summary>"Systematic review", "Multivocal literature review", ...</summary>
    public string Kind { get; init; } = "";
    /// <summary>The kind of review as the masthead names it; <see cref="Kind"/> when not set.</summary>
    public string? Series { get; init; }
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
    /// <summary>Records screened on title and abstract, as in the flow diagram; null when the kind of review has no such count.</summary>
    public int? Screened { get; init; }
    /// <summary>Included studies whose full text was read, as in the flow diagram; null when not known.</summary>
    public int? ReadInFull { get; init; }
    /// <summary>The citation verdicts as the report shows them (<see cref="VerdictTally.ForRun"/>); null when the run has none.</summary>
    public VerdictTally? Citations { get; init; }
    /// <summary>The self-check the run wrote to self-check.json (<see cref="GallerySelfCheck.Read"/>); null when it wrote none.</summary>
    public GallerySelfCheck? SelfCheck { get; init; }
    /// <summary>How many included studies are reviews (secondary studies), when the run records it; null shows no count.</summary>
    public int? ReviewsAmongIncluded { get; init; }
    /// <summary>What the retraction check found among the included studies, when the run made one; null shows no line.</summary>
    public RetractionTally? Retractions { get; init; }
    /// <summary>The SHA-256 fingerprint of protocol.md, or the short hash older runs recorded; empty when none.</summary>
    public string ProtocolFingerprint { get; init; } = "";
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    /// <summary>The report page, relative to the site (e.g. "spec-matrix/{id}").</summary>
    public string ReportPath { get; init; } = "";
    /// <summary>False for a gallery copy, which outlives its run: the page then offers no link into the run's files.</summary>
    public bool LinksToRun { get; init; } = true;
    public bool HasProtocol { get; init; }

    public string ArchivePath => $"api/workspace/{RunId}/archive";
    public string ProtocolPath => $"api/workspace/{RunId}/protocol.md";
    /// <summary>The decision log: every screened record with its decision and the reason, as RIS.</summary>
    public string DecisionLogPath => $"api/workspace/{RunId}/screened.ris";

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

    public string MastheadKind => string.IsNullOrWhiteSpace(Series) ? Kind : Series!;

    /// <summary>The tool's version number for the masthead and the byline note ("0.6.0"), or empty when not recorded.</summary>
    public string Version => ToolVersion.Number(AppVersion) is { Length: > 0 } v && v != "unknown" ? v : "";

    /// <summary>"Run 596541db · v0.6.0", the run as the masthead names it.</summary>
    public string RunLabel => $"Run {ShortRunId}" + (Version.Length > 0 ? $" · v{Version}" : "");

    /// <summary>The day the report was written, for "Generated ..." in the masthead.</summary>
    public DateTime? GeneratedUtc => CompletedUtc ?? SearchedUtc ?? ProtocolWrittenUtc;

    /// <summary>The note under the byline: the tool is software, the person who ran it is responsible, the model and how to cite the tool.</summary>
    public string BylineNote =>
        "Software, not an author. The person who ran this review is responsible for its use. " +
        $"Model: {(Models.Count == 0 ? "not recorded" : string.Join(", ", Models))}. " +
        $"Cite as software: TraceableAI{(Version.Length > 0 ? " v" + Version : "")}.";

    /// <summary>The abstract's paragraphs, with headings when it is structured.</summary>
    public IReadOnlyList<AbstractPart> AbstractParagraphs => AbstractParts.Split(Abstract);

    /// <summary>
    /// The groups of the Article info box, each with its label and its lines. A line whose data the run does not have is
    /// left out, and so is a group with no lines; every number is the one the flow diagram and the citation check give.
    /// </summary>
    public IReadOnlyList<(string Label, IReadOnlyList<string> Lines)> InfoGroups()
    {
        var groups = new List<(string, IReadOnlyList<string>)>();
        void Add(string label, params string?[] lines)
        {
            var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList();
            if (kept.Count > 0) groups.Add((label, kept));
        }
        Add("Article history",
            ProtocolWrittenUtc == null ? null : "Protocol fixed " + ShortDate(ProtocolWrittenUtc),
            SearchedUtc == null ? null : "Search run " + ShortDate(SearchedUtc),
            CompletedUtc == null ? null : "Report written " + ShortDate(CompletedUtc));
        Add("Records",
            $"{Count(Found)} identified",
            Screened is int screened ? $"{Count(screened)} screened" : null,
            $"{Count(Included)} included" + (ReviewsAmongIncluded is int reviews ? $" ({Count(reviews)} {(reviews == 1 ? "review" : "reviews")})" : ""),
            ReadInFull is int full && Included > 0 ? $"{Count(full)} of {Count(Included)} read in full" : null);
        Add("Checks",
            Citations is { Checked: > 0 } c ? $"Citations {Count(c.Supported)} of {Count(c.Checked)} supported" : null,
            SelfCheckLine(SelfCheck),
            Retractions?.Line);
        Add("Keywords", Keywords.ToArray());
        Add("Registration", Registration);
        return groups;
    }

    /// <summary>"Self-check: 8 of 11 passed; 2 notes, 1 fault", from self-check.json; null when the run wrote none.</summary>
    public static string? SelfCheckLine(GallerySelfCheck? check)
    {
        if (check == null) return null;
        int total = check.Passed + check.Notes + check.Faults;
        if (total == 0) return null;
        var rest = new List<string>();
        if (check.Notes > 0) rest.Add($"{Count(check.Notes)} {(check.Notes == 1 ? "note" : "notes")}");
        if (check.Faults > 0) rest.Add($"{Count(check.Faults)} {(check.Faults == 1 ? "fault" : "faults")}");
        return $"Self-check: {Count(check.Passed)} of {Count(total)} passed" + (rest.Count > 0 ? "; " + string.Join(", ", rest) : "");
    }

    internal static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"25 Nov 2026", the short date of the article history.</summary>
    public static string ShortDate(DateTime? utc) =>
        utc is DateTime d ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "not recorded";

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
    public static ArticleInfo ForSystematic(Guid runId, ReviewState? state, PrismaReport report, string title, string @abstract, bool hasProtocol,
        VerdictTally? citations = null, string? galleryPath = null, GallerySelfCheck? selfCheck = null, int? reviewsAmongIncluded = null, RetractionTally? retractions = null)
    {
        var models = new[] { state?.RunSettings?.Model, state?.RunSettings?.FastModel }
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        string fingerprint = state?.ProtocolSha256 is { Length: > 0 } sha ? sha : (report.ProtocolHash ?? "").Trim();
        return new ArticleInfo
        {
            RunId = runId,
            Title = title,
            Abstract = @abstract,
            Kind = SearchAdequacy.Kind(state),
            Series = "Systematic literature review",
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
            Screened = state?.Stats.Screened,
            ReadInFull = state?.Stats.FullTextRetrieved,
            Citations = citations,
            SelfCheck = selfCheck,
            ReviewsAmongIncluded = reviewsAmongIncluded,
            Retractions = retractions,
            ProtocolFingerprint = fingerprint == "not recorded" ? "" : fingerprint,
            Keywords = state == null ? Array.Empty<string>() : ArticleKeywords.From(state.SearchQuery,
                state.SearchPerspectives.Count > 0 ? state.SearchPerspectives : new List<string> { state.SearchQuery },
                state.ThematicSynthesis?.Themes.Select(t => t.Name)),
            ReportPath = galleryPath?.TrimStart('/') ?? $"spec-matrix/{runId}",
            LinksToRun = galleryPath == null,
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

/// <summary>
/// What a retraction check found among the included studies, for the front page's Checks group: how many it checked,
/// and how many were retracted, carry an expression of concern or were corrected. A kind of review fills it only from
/// the check's own file; without one there is no line, never a guessed "none".
/// </summary>
public sealed record RetractionTally(int Checked, int Retracted, int Concerns, int Corrected)
{
    /// <summary>The tally of a run's retraction-check.json; null when the run made no check or checked no study (a study that could not be checked is not counted).</summary>
    public static RetractionTally? From(RetractionCheckFile? file)
    {
        var done = file?.Studies.Where(s => s.Status != RetractionStatus.NotChecked).ToList();
        if (done == null || done.Count == 0) return null;
        return new(done.Count, done.Count(s => s.Status == RetractionStatus.Retracted), done.Count(s => s.Status == RetractionStatus.Concern),
            done.Count(s => s.Status == RetractionStatus.Corrected));
    }

    /// <summary>"Retractions: none in 38 checked" or "Retractions: 1 retracted, 2 corrected, of 38 checked"; null when none was checked.</summary>
    public string? Line
    {
        get
        {
            if (Checked <= 0) return null;
            var found = new List<string>();
            if (Retracted > 0) found.Add($"{ArticleInfo.Count(Retracted)} retracted");
            if (Concerns > 0) found.Add($"{ArticleInfo.Count(Concerns)} with an expression of concern");
            if (Corrected > 0) found.Add($"{ArticleInfo.Count(Corrected)} corrected");
            return found.Count == 0 ? $"Retractions: none in {ArticleInfo.Count(Checked)} checked" : $"Retractions: {string.Join(", ", found)}, of {ArticleInfo.Count(Checked)} checked";
        }
    }
}
