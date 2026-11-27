using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>Something the clean-up took out of, or changed in, a section's text.</summary>
public record FormattingChange(string Field, string Kind, string Text);

/// <summary>
/// Keeps the report's prose plain. The writing prompts ask for paragraphs only, but models still write
/// Markdown: "### Technical Challenges" headings, bullet lists and pipe tables, which the report page and
/// main.tex would show as raw symbols. This turns them back into prose:
///  - heading lines are removed (the paragraph that follows stays);
///  - list items become sentences of one paragraph, so their text and citations are kept and checked;
///  - tables are taken out of the prose (a table belongs in the artifact) and logged, never silently lost;
///  - emphasis, inline code and links lose their markup but keep their words.
/// It runs before the citation check, so the checked sentences are exactly the ones shown.
/// </summary>
public static class ProseCleaner
{
    /// <summary>The instruction every prose-writing prompt carries.</summary>
    public const string PlainProseRule =
        "Write plain paragraphs separated by a blank line: no Markdown, no headings, no bullet or numbered lists, no tables and no bold or italics.";

    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^\s{0,6}(?:[-*+•]|\d{1,2}[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The text as plain prose. <paramref name="source"/> is the word for one cited work ("study", "source"), used when a
    /// sentence opens with a bare marker.
    /// </summary>
    public static (string Text, List<FormattingChange> Changes) Clean(string? text, string field, string source = "study")
    {
        var changes = new List<FormattingChange>();
        if (string.IsNullOrWhiteSpace(text)) return (text ?? "", changes);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraphs = new List<string>();
        var current = new List<string>();
        var list = new List<string>();
        void FlushList()
        {
            if (list.Count == 0) return;
            current.Add(string.Join(" ", list.Select(EndAsSentence)));
            list.Clear();
        }
        void FlushParagraph()
        {
            FlushList();
            if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
            current.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) { FlushParagraph(); continue; }

            // A pipe table: a row followed by a |---|---| separator, plus all rows after it. A caption line
            // right before it ("Table 1. ...") goes with it.
            if (line.Count(c => c == '|') >= 2 && i + 1 < lines.Length && TableSeparator.IsMatch(lines[i + 1]))
            {
                FlushList();
                var table = new List<string>();
                if (current.Count > 0 && Regex.IsMatch(current[^1], @"^Table\s+\d+", RegexOptions.IgnoreCase))
                {
                    table.Add(current[^1]);
                    current.RemoveAt(current.Count - 1);
                }
                int j = i;
                while (j < lines.Length && lines[j].Count(c => c == '|') >= 2) table.Add(lines[j++].Trim());
                changes.Add(new FormattingChange(field, "table removed", string.Join("\n", table)));
                i = j - 1;
                FlushParagraph();
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                changes.Add(new FormattingChange(field, "heading removed", heading.Groups[1].Value));
                continue;
            }

            var item = ListItem.Match(line);
            if (item.Success)
            {
                if (list.Count == 0) changes.Add(new FormattingChange(field, "list turned into prose", line));
                list.Add(item.Groups[1].Value.Trim());
                continue;
            }

            FlushList();
            current.Add(line);
        }
        FlushParagraph();

        string result = string.Join("\n\n", paragraphs.Where(p => p.Length > 0));
        result = Regex.Replace(result, @"`([^`\n]+)`", "$1");                       // inline code
        result = Regex.Replace(result, @"\[([^\]\n]+)\]\((https?://[^)\s]+)\)", "$1");  // links, not [n] markers
        result = Regex.Replace(result, @"(?<=\s|^)#{1,6}\s+", "");                     // a heading marker left inside a line
        result = MethodsSectionWriter.StripMarkdownEmphasis(result);
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        result = CitationForm(result, field, source, changes);
        // Walk the sentences in order and copy the text between them unchanged, so paragraph breaks stay.
        var once = new StringBuilder(result.Length);
        int copied = 0;
        foreach (var sentence in CitationSupportChecker.SplitSentences(result))
        {
            int at = result.IndexOf(sentence, copied, StringComparison.Ordinal);
            if (at < 0) continue;
            string kept = DropRepeatedCitations(sentence);
            once.Append(result, copied, at - copied).Append(kept);
            copied = at + sentence.Length;
            if (kept != sentence) changes.Add(new FormattingChange(field, "repeated citation removed", sentence));
        }
        once.Append(result, copied, result.Length - copied);
        return (once.ToString().Trim(), changes);
    }

    private static readonly Regex Marker = new(@"\s*\[(\d+(?:\s*[,–-]\s*\d+)*)\]", RegexOptions.Compiled);

    // A marker written onto a word: "economics[4]".
    private static readonly Regex GluedMarker = new(@"(?<=[\p{L}\p{N}])(?=\[\d+(?:\s*[,–-]\s*\d+)*\])", RegexOptions.Compiled);

    // A marker that opens a sentence as its subject: "... value. [9] describes". Not after "et al.", "e.g." or "i.e.".
    private static readonly Regex OpeningMarker = new(
        @"(?<=(?:^|\n|(?<!\bal|\be\.g|\bi\.e)[.!?]["")]?[ \t]+))\[(\d+(?:\s*[,–-]\s*\d+)*)\](?=[ \t]+\p{Ll})", RegexOptions.Compiled);

    /// <summary>
    /// How the citations of a text are written (issue #725): markers side by side become one ("[9][2][11]" -> "[2, 9, 11]"),
    /// a marker gets a space before it, and a sentence that opens with a bare marker names what it is ("[9] describes" ->
    /// "Study [9] describes"), as the rule "name a study by its marker" asks. Each change is listed.
    /// </summary>
    private static string CitationForm(string text, string field, string source, List<FormattingChange> changes)
    {
        string merged = CitationMarkers.MergeAdjacent(text);
        if (merged != text) changes.Add(new FormattingChange(field, "citations side by side merged", text));
        string spaced = GluedMarker.Replace(merged, " ");
        if (spaced != merged) changes.Add(new FormattingChange(field, "space put before a citation", merged));
        string named = OpeningMarker.Replace(spaced, m =>
        {
            string word = CitationSupportChecker.ReferencesIn(m.Value).Count < 2 ? source
                : source.EndsWith('y') ? source[..^1] + "ies" : source + "s";
            return char.ToUpperInvariant(word[0]) + word[1..] + " " + m.Value;
        });
        if (named != spaced) changes.Add(new FormattingChange(field, "citation that opened a sentence named", spaced));
        return named;
    }

    /// <summary>
    /// Removes the citation at the end of a sentence for a study the sentence already names, so "[5] warns that X [5]."
    /// becomes "[5] warns that X." (issue #125). A study is named when its marker is read as a word of the sentence:
    /// followed by a word, as in "[5] warns" or "Study [6] reports". Only the sentence's last marker loses references,
    /// and only when nothing but punctuation follows it; a marker that ends one clause and is repeated at the end of
    /// the next ("A [3]; B [3].") stays, because there each marker carries a claim of its own. Public for unit tests.
    /// </summary>
    public static string DropRepeatedCitations(string sentence)
    {
        sentence = DropRepeatedInClause(sentence);
        var markers = Marker.Matches(sentence).Cast<Match>().ToList();
        if (markers.Count < 2) return sentence;
        var last = markers[^1];
        if (sentence[(last.Index + last.Length)..].Any(char.IsLetterOrDigit)) return sentence;

        var named = new HashSet<int>();
        foreach (var m in markers.Take(markers.Count - 1))
        {
            string after = sentence[(m.Index + m.Length)..].TrimStart();
            // "[3] and [7] show" names [7] (the subject's verb follows it) but not [3], which a later claim may still need.
            if (after.Length > 0 && char.IsLetter(after[0]) && !Regex.IsMatch(after, @"^(and|or|nor)\b", RegexOptions.IgnoreCase))
                named.UnionWith(CitationSupportChecker.ReferencesIn(m.Value));
        }
        var lastRefs = CitationSupportChecker.ReferencesIn(last.Value);
        if (!lastRefs.Any(named.Contains)) return sentence;

        var keep = lastRefs.Where(r => !named.Contains(r)).ToList();
        string replacement = keep.Count == 0 ? "" : $" [{string.Join(", ", keep)}]";
        return sentence[..last.Index] + replacement + sentence[(last.Index + last.Length)..];
    }

    /// <summary>
    /// The same inside a sentence (issue #568): "[4] identifies A and B [4], while [1] highlights C" becomes "[4] identifies
    /// A and B, while [1] highlights C". A marker goes when the marker just before it names the same study and it closes
    /// that clause: punctuation, "and", "or", "but", "while" or "whereas" follows it.
    /// </summary>
    private static string DropRepeatedInClause(string sentence)
    {
        var markers = Marker.Matches(sentence).Cast<Match>().ToList();
        var kept = new StringBuilder(sentence);
        for (int i = markers.Count - 2; i >= 1; i--)
        {
            Match previous = markers[i - 1], m = markers[i];
            string afterPrevious = sentence[(previous.Index + previous.Length)..].TrimStart();
            bool names = afterPrevious.Length > 0 && char.IsLetter(afterPrevious[0]) && !Regex.IsMatch(afterPrevious, @"^(and|or|nor)\b", RegexOptions.IgnoreCase);
            string after = sentence[(m.Index + m.Length)..].TrimStart();
            bool closes = after.Length == 0 || ",;:)".Contains(after[0]) || Regex.IsMatch(after, @"^(and|or|but|while|whereas)\b", RegexOptions.IgnoreCase);
            var refs = CitationSupportChecker.ReferencesIn(m.Value);
            if (!names || !closes || !refs.All(CitationSupportChecker.ReferencesIn(previous.Value).Contains)) continue;
            kept.Remove(m.Index, m.Length); // from the end backwards, so the earlier markers keep their positions
        }
        return kept.ToString();
    }

    private static string EndAsSentence(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return s;
        // "Technical: Prioritise ..." stays as it is; a fragment without an end mark gets a full stop.
        return Regex.IsMatch(s, @"[.!?]$|[.!?]\s*\[[\d,\s–-]+\]$") ? s : s + ".";
    }
}
