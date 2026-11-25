using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Places where LaTeX may break a long word of escaped text, so a web address, a DOI or a commit hash ends at the
/// margin instead of running past it (#505). TeX breaks a line only at a space or a hyphen, so an
/// address of 80 characters with neither sticks out of the column, the table cell or the page; this is the cause of
/// every overfull line in a real paper's reference list and appendix. Only long words are touched: a break is allowed
/// after "/", ".", "_", "=", "?", "&amp;", "#" and ":" inside them, and every 6 characters of a run of letters and digits
/// that holds a digit (a hash, an archive time stamp). A break here prints nothing: no hyphen is added to an address.
/// </summary>
public static class LatexBreaks
{
    /// <summary>A word this long or longer gets break points; shorter words fit any column.</summary>
    public const int LongWord = 20;

    /// <summary>What is written at a break point: a zero-width place where TeX may end the line.</summary>
    public const string Break = @"\allowbreak{}";

    // A run of letters and digits with a digit in it, not part of a command name ("\textasciicircum").
    private static readonly Regex Code = new(@"(?<![\\A-Za-z0-9])(?=[A-Za-z]*[0-9])[A-Za-z0-9]{12,}", RegexOptions.Compiled);
    // A separator inside an address: "/", ".", "=", "?", ":" and the escaped "\_", "\&", "\#", when a letter, a digit or
    // an escaped character follows; never before punctuation, so a line does not start with ")" or ";".
    private static readonly Regex Separator = new(@"(\\[_&#]|[/.=?:])(?=[A-Za-z0-9\\])", RegexOptions.Compiled);

    /// <summary>The escaped text with break points in each word of <see cref="LongWord"/> characters or more.</summary>
    public static string Allow(string escaped)
    {
        if (string.IsNullOrEmpty(escaped)) return escaped;
        return Regex.Replace(escaped, @"\S{" + LongWord + ",}", m => InWord(m.Value));
    }

    private static string InWord(string word)
    {
        string chunked = Code.Replace(word, m => Chunk(m.Value));
        return Separator.Replace(chunked, m => m.Value + Break);
    }

    private static string Chunk(string run)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < run.Length; i += 6) parts.Add(run.Substring(i, System.Math.Min(6, run.Length - i)));
        return string.Join(Break, parts);
    }
}
