using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Themes that build on each other instead of restating each other, and sources that are not stretched to fit a theme
/// (issues #674 and #675). In run 8942b961 the theme "Evaluation Methods and Empirical Evidence" held 8 of the 9 studies and
/// repeated the other themes' findings with their figures, and a virtual focus group was presented as a safety mechanism.
/// Each theme is now told which of its sources earlier themes already hold, under which codes, so it refers to those
/// findings instead of restating them; a broad theme is asked to compare how its sources approach the question; and a
/// source that bears on the theme only indirectly may be said to do so, or left out with its reason on file.
/// </summary>
public static partial class ReviewWriter
{
    private sealed class ThemeAnswer
    {
        public string? Heading { get; set; }
        public string? Text { get; set; }
        public List<LeftOutAnswer>? LeftOut { get; set; }
    }

    private sealed class LeftOutAnswer { public int Ref { get; set; } public string? Reason { get; set; } }

    /// <summary>A theme that holds more than this share of the review's themed sources is broad: it compares, it does not list.</summary>
    public const double BroadThemeShare = 0.5;

    /// <summary>At most this many lines of earlier themes' codes go to one theme's prompt.</summary>
    public const int MaxEarlierLines = 30;

    /// <summary>Whether a theme holds most of the sources of the review's themes (and there is more than one theme).</summary>
    public static bool IsBroadTheme(ReviewEvidence evidence, EvidenceTheme theme)
    {
        int themed = evidence.Themes.SelectMany(t => t.Sources).Distinct().Count();
        return evidence.Themes.Count > 1 && theme.Sources.Count >= 4 && theme.Sources.Count > themed * BroadThemeShare;
    }

    /// <summary>
    /// What the themes before this one hold of its sources: for each source of this theme that an earlier theme holds too,
    /// the codes (or, in a lean review, the findings) that put it there, by theme. Empty for the first theme and when no
    /// source is shared. The codes are model text, so the caller wraps the block with <see cref="PromptSafety.Wrap"/>.
    /// </summary>
    public static string EarlierThemesEvidence(ReviewEvidence evidence, EvidenceTheme theme)
    {
        var lines = new List<string>();
        foreach (var earlier in evidence.Themes.TakeWhile(t => t.Id != theme.Id))
            foreach (int r in theme.Sources.Where(earlier.Sources.Contains))
            {
                var ids = earlier.FindingIds.GetValueOrDefault(r);
                var what = ids is { Count: > 0 }
                    ? (evidence.Source(r)?.Findings ?? Array.Empty<EvidenceFinding>()).Where(f => ids.Contains(f.Id)).Select(f => f.Value).ToList()
                    : (earlier.Codes.GetValueOrDefault(r) ?? Array.Empty<string>()).ToList();
                if (what.Count > 0) lines.Add($"Under \"{earlier.Name}\": [{r}] {string.Join("; ", what)}");
            }
        if (lines.Count <= MaxEarlierLines) return string.Join("\n", lines);
        return string.Join("\n", lines.Take(MaxEarlierLines)) + $"\n(and {lines.Count - MaxEarlierLines} more)";
    }

    /// <summary>The theme prompt's block with what earlier themes hold of its sources, or empty when they hold none.</summary>
    public static string EarlierThemesBlock(ReviewEvidence evidence, EvidenceTheme theme, WritingProfile profile)
    {
        string earlier = EarlierThemesEvidence(evidence, theme);
        return earlier.Length == 0 ? ""
            : "\n\n" + PromptSafety.Wrap(earlier, $"what the earlier themes report of this theme's {profile.Sources}: the codes or findings that put each one there") + "\n";
    }

    /// <summary>
    /// The theme prompt's rule on building on the earlier themes, on a broad theme, and on a source that fits the theme
    /// only indirectly (issues #674 and #675).
    /// </summary>
    public static string RedThreadRule(WritingProfile profile, ReviewEvidence evidence, EvidenceTheme theme)
    {
        string s = profile.Source, ss = profile.Sources;
        var rule = new List<string>();
        if (EarlierThemesEvidence(evidence, theme).Length > 0)
            rule.Add($"A finding the earlier themes report (listed above) is written up there: refer to it briefly by that theme's name (\"as reported under ...\") with its citation, and do not restate it with its figures; say only what it adds to this theme.");
        if (IsBroadTheme(evidence, theme))
            rule.Add($"This theme holds {theme.Sources.Count} of the review's {evidence.Themes.SelectMany(t => t.Sources).Distinct().Count()} {ss}, so compare how the {ss} evaluate or approach the question (their designs, measures, settings and what each leaves out) instead of listing each {s}'s result again.");
        rule.Add($"Never stretch a {s} to fit the theme: when its evidence bears on the theme only indirectly, say so plainly (\"[9], a study of focus groups, bears on this only indirectly\"), or leave it out and list it in \"leftOut\" with the reason; the review keeps that reason on file.");
        return string.Join(" ", rule);
    }

    /// <summary>Why the sources a theme's writer left out cannot be accepted, or null.</summary>
    private static string? LeftOutProblem(List<LeftOutAnswer>? leftOut, string text, IReadOnlyList<int> sources) =>
        LeftOutProblem((leftOut ?? new()).Select(l => (l.Ref, l.Reason)).ToList(), text, sources);

    /// <summary>
    /// Why the sources a theme's writer left out cannot be accepted, or null: each must be a source of the theme, not cited
    /// in the text, given once, with a reason of at least three words. Public for unit tests.
    /// </summary>
    public static string? LeftOutProblem(IReadOnlyList<(int Ref, string? Reason)> leftOut, string text, IReadOnlyList<int> sources)
    {
        var cited = ThematicSynthesis.CitedIn(text);
        foreach (var (r, reason) in leftOut)
        {
            if (!sources.Contains(r)) return $"leftOut names [{r}], which is not a source of this theme.";
            if (cited.Contains(r)) return $"leftOut names [{r}], but the text cites it; leave it out of the text or out of leftOut.";
            if ((reason ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3) return $"leftOut gives no reason for [{r}]; say in a short sentence why it bears on the theme only indirectly.";
        }
        var twice = leftOut.GroupBy(l => l.Ref).FirstOrDefault(g => g.Count() > 1);
        return twice == null ? null : $"leftOut names [{twice.Key}] more than once.";
    }

    /// <summary>The figures of a text that a result gives (a percentage, percentage points, a fold change), written alike.</summary>
    public static HashSet<string> UnitFigures(string text) =>
        QuotedFigure.Matches(Regex.Replace(text ?? "", @"\[[^\]]*\]", ""))
            .Select(m => Regex.Replace(m.Value.ToLowerInvariant(), @"\s+|percent\b|per\s*cent\b", m2 => m2.Value.Trim().Length == 0 ? "" : "%").Replace("-fold", "fold"))
            .ToHashSet();
}
