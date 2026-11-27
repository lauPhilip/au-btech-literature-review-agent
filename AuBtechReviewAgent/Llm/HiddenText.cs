using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace AuBtechReviewAgent;

/// <summary>
/// What was taken out of a source's text because a reader never sees it (#369), on file beside the injection flags.
/// </summary>
/// <param name="InvisibleCharacters">Invisible characters removed (zero-width, bidirectional controls, tag characters).</param>
/// <param name="HiddenElements">Parts of a web page hidden by their attributes or style, or runs of letters a PDF hides (#534).</param>
public sealed record HiddenTextRemoved(int InvisibleCharacters, int HiddenElements)
{
    /// <summary>A record of what was removed, or null when nothing was, so a clean source leaves no field on file.</summary>
    public static HiddenTextRemoved? Of(int invisibleCharacters, int hiddenElements) =>
        invisibleCharacters + hiddenElements > 0 ? new(invisibleCharacters, hiddenElements) : null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (InvisibleCharacters > 0) parts.Add($"{InvisibleCharacters} invisible character{(InvisibleCharacters == 1 ? "" : "s")}");
        if (HiddenElements > 0) parts.Add($"{HiddenElements} hidden part{(HiddenElements == 1 ? "" : "s")} of the page");
        return string.Join(" and ", parts);
    }
}

/// <summary>The text a reader sees, and what was taken out of it because a reader would not see it.</summary>
/// <param name="Hidden">The readable text that was hidden (hidden page parts, text spelled in tag characters), for the scan.</param>
public sealed record CleanedText(string Text, int InvisibleCharacters, int HiddenElements, string Hidden);

/// <summary>
/// Takes hidden text out of a source's text before it is kept or read by the model (#369). A page or a paper can
/// carry text a human reader never sees but the model does: invisible Unicode characters, which can also split a word
/// so the phrase scan misses it ("ig&#x200B;nore previous instructions"), and on a web page, text hidden by styling.
/// Everything else is kept exactly as it was, so a quote from the cleaned text is found in the cleaned text, which is
/// the text that is kept and checked. Pure functions; the callers record the counts.
/// </summary>
public static partial class HiddenText
{
    /// <summary>The flag added when the removed hidden text reads like instructions to the model.</summary>
    public const string FlagPattern = "hidden-text";

    private const char ZeroWidthNonJoiner = '\u200C', ZeroWidthJoiner = '\u200D', SoftHyphen = '\u00AD';

    /// <summary>
    /// Characters that show nothing: zero-width space and marks, word joiner and invisible operators, bidirectional
    /// embeddings, overrides and isolates, the BOM, and the Mongolian vowel separator.
    /// </summary>
    public static bool IsInvisible(char c) => c is '\u180E' or (>= '\u200B' and <= '\u200F') or (>= '\u202A' and <= '\u202E')
        or (>= '\u2060' and <= '\u2064') or (>= '\u2066' and <= '\u2069') or '\uFEFF';

    /// <summary>
    /// Removes invisible characters and counts them. The joiners U+200C and U+200D are kept between two characters
    /// outside ASCII, where emoji sequences and Arabic or Indic scripts need them, and removed next to an ASCII
    /// character, where they can only split a word. A soft hyphen is removed but not counted: it is an ordinary
    /// hyphenation hint and can hide nothing. Tag characters (U+E0000 to U+E007F) are removed and counted, and the
    /// ASCII they spell is returned as hidden text, since they are a known way to smuggle a message to a model.
    /// </summary>
    public static CleanedText RemoveInvisible(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new CleanedText("", 0, 0, "");
        // Taking characters out can bring the two halves of a tag character together (a high surrogate, an invisible
        // character, a low surrogate), so it goes on until a pass removes nothing (#623). Each pass that removes shortens the text.
        var current = new CleanedText(text, 0, 0, "");
        var spelled = new StringBuilder();
        int removed = 0;
        while (true)
        {
            var pass = RemoveOnce(current.Text);
            if (pass.Text.Length == current.Text.Length) break;
            removed += pass.InvisibleCharacters;
            spelled.Append(pass.Hidden);
            current = pass;
        }
        return new CleanedText(current.Text, removed, 0, spelled.ToString());
    }

    private static CleanedText RemoveOnce(string text)
    {
        if (!text.Any(c => IsInvisible(c) || c == SoftHyphen || char.IsHighSurrogate(c))) return new CleanedText(text, 0, 0, "");

        var kept = new StringBuilder(text.Length);
        var spelled = new StringBuilder();
        int removed = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\uDB40' && i + 1 < text.Length && text[i + 1] is >= '\uDC00' and <= '\uDC7F')
            {
                char ascii = (char)(text[i + 1] - '\uDC00');
                if (ascii is >= ' ' and <= '~') spelled.Append(ascii);
                removed++;
                i++;
                continue;
            }
            if (c == SoftHyphen) continue;
            if (c is ZeroWidthNonJoiner or ZeroWidthJoiner) { kept.Append(c); continue; }
            if (IsInvisible(c)) { removed++; continue; }
            kept.Append(c);
        }
        string visible = KeepJoinersBetweenNonAscii(kept, ref removed);
        return new CleanedText(visible, removed, 0, spelled.ToString());
    }

    /// <summary>
    /// Decides on the joiners only after everything else is out, so a soft hyphen or a tag character between a joiner and an
    /// ASCII character cannot hide that it is next to one (#623). A run of joiners stays only when the character before it
    /// and the one after it are both outside ASCII, which also makes a second pass change nothing.
    /// </summary>
    private static string KeepJoinersBetweenNonAscii(StringBuilder text, ref int removed)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not (ZeroWidthNonJoiner or ZeroWidthJoiner)) { result.Append(text[i]); continue; }
            int end = i;
            while (end < text.Length && text[end] is ZeroWidthNonJoiner or ZeroWidthJoiner) end++;
            if (i > 0 && end < text.Length && text[i - 1] > '\u007F' && text[end] > '\u007F') result.Append(text.ToString(i, end - i));
            else removed += end - i;
            i = end - 1;
        }
        return result.ToString();
    }

    /// <summary>
    /// The full text of a paper (its chunks) with invisible characters removed, the count, and the text they spelled.
    /// </summary>
    public static (List<DocumentChunk> Chunks, HiddenTextRemoved? Removed, string Hidden) CleanChunks(IEnumerable<DocumentChunk> chunks)
    {
        var cleaned = new List<DocumentChunk>();
        var hidden = new StringBuilder();
        int removed = 0;
        foreach (var chunk in chunks)
        {
            var clean = RemoveInvisible(chunk.Text);
            removed += clean.InvisibleCharacters;
            if (clean.Hidden.Length > 0) hidden.Append(clean.Hidden).Append('\n');
            cleaned.Add(clean.InvisibleCharacters == 0 ? chunk : chunk with { Text = clean.Text });
        }
        return (cleaned, HiddenTextRemoved.Of(removed, 0), hidden.ToString());
    }

    /// <summary>
    /// The flags for a source's kept text: every instruction-like phrase in the cleaned text, plus one
    /// <see cref="FlagPattern"/> flag, with its phrase, when the hidden text that was taken out holds a strong phrase,
    /// one aimed at the model (<see cref="PromptSafety.IsStrong"/>). Null when there is neither. Like the other flags,
    /// it changes no decision.
    /// </summary>
    public static List<SourceFlag>? Flags(string? keptText, string? hiddenText)
    {
        var flags = PromptSafety.Find(keptText);
        if (PromptSafety.Find(hiddenText)?.FirstOrDefault(f => PromptSafety.IsStrong(f.Pattern)) is { } hidden)
            (flags ??= new()).Add(new SourceFlag(FlagPattern, hidden.Excerpt));
        return flags;
    }
}
