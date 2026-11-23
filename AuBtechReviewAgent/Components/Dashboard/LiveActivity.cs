using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// One short line under a running review's title that says what is happening right now ("Screening record 120 of
/// 431"), with a small running count ("431 found · 42 included"). <see cref="Kind"/> names the sort of work; the
/// screen-reader announcement (<see cref="Announce"/>) depends on it alone, so it changes when the work changes and
/// not with every count.
/// </summary>
public sealed record LiveLine(string Kind, string Text, string? Count, string Announce);

/// <summary>
/// Turns the progress a systematic run already reports (its step, the engine's detail line, the counts in
/// <see cref="ReviewStats"/> and <see cref="ReviewStats.ProgressSubject"/>) into a <see cref="LiveLine"/>. Nothing
/// here calls a model; it only rewords what the engine said. Titles, queries and theme names are data: the line is
/// shown as text, and long ones are cut (<see cref="Cut"/>). The multivocal review has its own mapping
/// (<see cref="MultivocalLiveActivity"/>).
/// </summary>
public static class LiveActivity
{
    /// <summary>The longest title or query the line shows, in characters, before it is cut with an ellipsis.</summary>
    public const int MaxSubject = 60;

    private static readonly Regex OfPattern = new(@"(\d+) of (\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex SearchSubject = new("^(?<name>[^:\"]{1,60}): \"(?<text>.*)\"$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>The line for a systematic run; null when the run is finished or stopped.</summary>
    public static LiveLine? ForSystematic(ReviewStats stats, string stage, int queuePosition)
    {
        if (PrismaReviewEngine.IsFinishedStage(stage) || stats.ProgressStep == RunProgress.Done) return null;
        if (stage == PrismaReviewEngine.StageQueued)
            return Line("queue", queuePosition > 0 ? $"Waiting in line for a free run slot, number {queuePosition}" : "Waiting in line for a free run slot", null);
        if (stage == PrismaReviewEngine.StageAwaitingReview || stats.ProgressStep == RunProgress.Review)
            return Line("your-check", "Waiting for you to check the screening decisions", Counts(stats));

        string detail = stats.ProgressDetail ?? "";
        var of = Of(detail);
        string subject = stats.ProgressSubject ?? "";
        string? count = Counts(stats);
        switch (stats.ProgressStep)
        {
            case RunProgress.Search:
                if (SearchFor(subject) is { } searching) return Line("search", searching, of is { } s ? $"search {s.Done} of {s.Total}" : count);
                if (detail.StartsWith("Preparing", StringComparison.Ordinal)) return Line("search-prepare", "Preparing the search strings", null);
                return Line("search", "Searching the databases", of is { } p ? $"search {p.Done} of {p.Total}" : count);
            case RunProgress.Screening:
                return Line("screen", of is { } r ? $"Screening record {Next(r)} of {r.Total}" : "Screening the records", count);
            case RunProgress.Chaining:
                if (detail.StartsWith("Building out", StringComparison.Ordinal)) return Line("chain", "Building out from the most-cited included studies", count);
                if (detail.Contains("seed studies", StringComparison.Ordinal) && of is { } seed)
                    return Line("chain", $"Following the citations of seed study {Next(seed)} of {seed.Total}", count);
                if (detail.Contains("records screened", StringComparison.Ordinal) && of is { } c)
                    return Line("chain-screen", $"Screening record {Next(c)} of {c.Total} found by citation chaining", count);
                return Line("chain", of is { } l ? $"Following citations: look-up {Next(l)} of {l.Total}" : "Looking up the references and citing papers of the included studies", count);
            case RunProgress.FullText:
                return Line("fulltext", subject.Length > 0 ? $"Reading the full text of {Cut(subject)}"
                    : of is { } f ? $"Reading full text {Next(f)} of {f.Total}" : "Fetching the open-access full texts", count);
            case RunProgress.Extraction:
                return Line("extract", of is { } x ? $"Extracting and appraising study {Next(x)} of {x.Total}" : "Extracting and appraising the included studies", count);
            case RunProgress.Synthesis:
                return Writing(detail, of, subject, stats);
            case RunProgress.Checking:
                if (detail.StartsWith("Repairing", StringComparison.Ordinal)) return Line("repair", "Repairing the citations that failed the check", CheckedCount(stats));
                if (detail.StartsWith("Second check", StringComparison.Ordinal) && of is { } sc)
                    return Line("check-second", $"Checking the citations again: paper {Next(sc)} of {sc.Total}", CheckedCount(stats));
                return Line("check", of is { } k ? $"Checking the citations: paper {Next(k)} of {k.Total}" : "Checking every citation against its paper", CheckedCount(stats));
            default:
                return Line("protocol", "Writing the protocol and fixing its fingerprint", null);
        }
    }

    private static LiveLine Writing(string detail, (int Done, int Total)? of, string subject, ReviewStats stats)
    {
        string? themes = stats.ThemesIdentified > 0 ? Plural(stats.ThemesIdentified, "theme") : null;
        if (detail.StartsWith("Coding", StringComparison.Ordinal))
            return Line("code", of is { } b ? $"Coding the studies: batch {Next(b)} of {b.Total}" : "Coding the included studies", Counts(stats));
        if (detail.StartsWith("Grouping", StringComparison.Ordinal)) return Line("themes", "Grouping the codes into themes", null);
        if (detail.StartsWith("Theme subsections", StringComparison.Ordinal) || detail.StartsWith("Writing", StringComparison.Ordinal) && detail.EndsWith("theme subsections", StringComparison.Ordinal))
            return Line("theme-write", subject.Length > 0 ? $"Writing the theme “{Cut(subject)}”"
                : of is { } t ? $"Writing theme {Next(t)} of {t.Total}" : "Writing the theme subsections", themes);
        if (detail.Contains("discussion", StringComparison.Ordinal)) return Line("discussion", "Writing the discussion", themes);
        if (detail.Contains("peer review", StringComparison.OrdinalIgnoreCase)) return Line("peer-review", "Reading the draft as a peer reviewer would", themes);
        if (detail.StartsWith("Title", StringComparison.Ordinal)) return Line("front", "Writing the title, abstract and introduction", themes);
        if (detail.Contains("artifact", StringComparison.Ordinal)) return Line("artifact", "Building the artifact", themes);
        return Line("draft", "Drafting the outline of the report", themes);
    }

    /// <summary>"Searching OpenAlex for “agentic AI”" from a subject such as <c>OpenAlex: "agentic AI"</c>; null for anything else.</summary>
    internal static string? SearchFor(string subject)
    {
        var m = SearchSubject.Match(subject ?? "");
        if (!m.Success) return null;
        string name = m.Groups["name"].Value.Trim(), text = m.Groups["text"].Value.Trim();
        return name.Length == 0 || text.Length == 0 ? null : $"Searching {name} for “{Cut(text)}”";
    }

    /// <summary>The subject the search reports while it asks one database: the database's name and the search string.</summary>
    public static string SearchSubjectOf(string sourceName, string query) => $"{sourceName}: \"{query}\"";

    /// <summary>
    /// "Bosschaerts et al., 2025" for a paper: the first author's family name, "and" or "et al." for more, and the
    /// year; the title in quotes, cut, when there are no authors. Built in code from the record, never by the model.
    /// </summary>
    public static string ShortCite(IReadOnlyList<string>? authors, string? publishedDate, string? title)
    {
        var names = (authors ?? Array.Empty<string>()).Select(FamilyName).Where(n => n.Length > 0).ToList();
        int year = ApaCitationBuilder.ExtractYear(publishedDate);
        string who = names.Count switch
        {
            0 => "",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{names[0]} et al.",
        };
        if (who.Length == 0) return $"“{Cut(title ?? "an untitled record")}”";
        return year > 0 ? $"{who}, {year}" : who;
    }

    private static string FamilyName(string raw)
    {
        string name = Regex.Replace(raw ?? "", @"\s+", " ").Trim(' ', ',', '.');
        if (name.Length == 0) return "";
        if (name.Contains(',')) return name.Split(',')[0].Trim();
        return name.Split(' ')[^1];
    }

    /// <summary>A title, query or name cut to <see cref="MaxSubject"/> characters, on a word where it can be, with an ellipsis; line breaks become spaces.</summary>
    public static string Cut(string text, int max = MaxSubject)
    {
        string flat = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (flat.Length <= max) return flat;
        string cut = flat[..max];
        int space = cut.LastIndexOf(' ');
        if (space > max / 2) cut = cut[..space];
        return cut.TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    /// <summary>"431 found · 120 screened · 42 included": the counts the run has so far, left out while they are 0.</summary>
    public static string? Counts(ReviewStats stats)
    {
        var parts = new List<string>();
        if (stats.TotalIdentified > 0) parts.Add($"{stats.TotalIdentified} found");
        if (stats.Screened > 0) parts.Add($"{stats.Screened} screened");
        if (stats.Included > 0) parts.Add($"{stats.Included} included");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? CheckedCount(ReviewStats stats) => stats.CitationsChecked > 0 ? $"{stats.CitationsChecked} citations checked" : Counts(stats);

    /// <summary>The first "n of m" in an engine's detail line.</summary>
    internal static (int Done, int Total)? Of(string? detail)
    {
        var m = OfPattern.Match(detail ?? "");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int done) || !int.TryParse(m.Groups[2].Value, out int total) || total <= 0) return null;
        return (Math.Clamp(done, 0, total), total);
    }

    /// <summary>The item being worked on after <c>Done</c> are finished, never past the last.</summary>
    internal static int Next((int Done, int Total) of) => Math.Min(of.Done + 1, of.Total);

    internal static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>A line whose announcement is fixed by its kind.</summary>
    internal static LiveLine Line(string kind, string text, string? count) => new(kind, text, count, AnnounceFor(kind));

    /// <summary>What a screen reader hears when the work changes: one sentence per kind, without numbers.</summary>
    public static string AnnounceFor(string kind) => kind switch
    {
        "queue" => "The review is waiting in line for a free slot.",
        "your-check" => "The review is waiting for you to check the screening decisions.",
        "protocol" => "Now writing the protocol.",
        "search-prepare" or "search" => "Now searching.",
        "screen" => "Now screening the records.",
        "chain" or "chain-screen" => "Now following citations.",
        "fulltext" => "Now reading the full texts.",
        "extract" => "Now extracting and appraising the studies.",
        "quality" => "Now scoring the quality of the sources.",
        "map" => "Now building the systematic map.",
        "code" or "themes" or "synthesis" => "Now finding the themes.",
        "theme-write" or "discussion" or "front" or "draft" or "peer-review" or "writing" => "Now writing the review.",
        "artifact" => "Now building the artifact.",
        "check" or "check-second" or "repair" => "Now checking the citations.",
        _ => "The review is running.",
    };
}

/// <summary>
/// Keeps the screen-reader announcement of a live line calm: it changes only when the sentence it would say changes
/// (a new kind of work, not a new count), and at most once every <see cref="MinGap"/>, so work that flips back and
/// forth is not read out each time. A change held back is given on the next call after the gap.
/// </summary>
public sealed class LiveAnnouncer
{
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(3);
    private string _said = "";
    private DateTime _saidAt = DateTime.MinValue;

    /// <summary>The text for the live region now.</summary>
    public string Next(string? wanted, DateTime nowUtc)
    {
        wanted ??= "";
        if (wanted == _said) return _said;
        if (_said.Length > 0 && nowUtc - _saidAt < MinGap) return _said;
        _said = wanted;
        _saidAt = nowUtc;
        return _said;
    }
}
