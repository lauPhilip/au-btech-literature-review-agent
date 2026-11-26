using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// How text from the run becomes LaTeX. Every piece of text from a source, the model or a user goes through one of
/// these, so none of it can run a command: a backslash, a brace, "%", "#", "^" and "~" are written as characters, never
/// as LaTeX. The escaped text also follows LaTeX's typography (issue #537): ``quotes'', an en dash in a range of numbers,
/// a non-breaking space before a citation, and the spaces after an abbreviation and an acronym that ends a sentence.
/// </summary>
public static partial class PaperLatex
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    // A range of numbers written with a hyphen ("2019-2025", "pp. 12-34"), not part of a date, an address or a name.
    private static readonly Regex NumberRange = new(@"(?<![\w.\-])(\d{1,4})-(\d{1,4})(?![\w\-]|\.\d)", RegexOptions.CultureInvariant, Timeout);
    // An abbreviation that ends with a full stop inside a sentence: TeX would otherwise leave a sentence's space after it.
    private static readonly Regex Abbreviation = new(@"\b(e\.g|i\.e|cf|vs|approx)\. ", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Orcid = new(@"\b(\d{4})-(\d{4})-(\d{4})-(\d{3}[\dX])\b", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex EtAl = new(@"\bet al\. ", RegexOptions.CultureInvariant, Timeout);
    // An acronym or name that ends a sentence ("an LLM. The", "TraceableAI. It"): TeX takes a capital before a full
    // stop for an initial. A lone capital after a space ("J. Smith") is an initial and is left alone.
    private static readonly Regex AcronymStop = new(@"(?<=[A-Za-z])([A-Z])\.(?=\s+[A-Z(`])", RegexOptions.CultureInvariant, Timeout);
    // A question mark standing alone, such as MMAT's "can't tell": braced, so it is not read as punctuation after a space.
    private static readonly Regex LoneMark = new(@"(?<=\s)\?(?=\s|$)", RegexOptions.CultureInvariant, Timeout);
    // A citation marker as CitationMarkers.Tidy writes it: "[1]", "[1, 2]", "[3–5, 8]".
    private static readonly Regex Marker = new(@"( ?)\[(\d{1,4}(?:(?:, |–)\d{1,4})*)\]", RegexOptions.CultureInvariant, Timeout);
    // A sentence end inside a paragraph: a full stop, question or exclamation mark after a small letter, a digit or a
    // closing bracket or quote, then a space and a capital. An abbreviation is followed by "\ " and never matches.
    private static readonly Regex SentenceEnd = new(@"(?<=[a-z0-9)\]}'][.!?](?:'')?) (?=[A-Z`\\])", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex WebAddress = new(@"https?://[^\s]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout);
    private static readonly Regex AddressPlaceholder = new(@"zzurl(\d+)zz", RegexOptions.CultureInvariant, Timeout);
    // A full stop after a word or a brace, then a command: the end of an element of a reference or a table cell.
    private static readonly Regex ElementStop = new(@"(?<=[\w}])\. (?=\\)", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex MathSpan = new(@"(?<!\\)\$([^$]+)\$", RegexOptions.CultureInvariant, Timeout);

    /// <summary>
    /// Escapes plain text for LaTeX, turns straight quotes into LaTeX quotes and *italics* into \textit, and citation
    /// markers into \cite of the reference keys "ref1", "ref2", ..., which <see cref="Build"/> resolves. The text keeps
    /// its closing full stop; only trailing spaces are removed.
    /// </summary>
    public static string Escape(string input) => Escape(input, ' ');

    /// <summary>
    /// Plain text as a paragraph of the paper: <see cref="Escape"/>, with one sentence per line, so a change in Overleaf
    /// or in version control touches one line. A line break is a space to LaTeX, so the printed text is the same.
    /// </summary>
    public static string Prose(string input) => Escape(input, ' ', prose: true);

    /// <summary>
    /// A heading: escaped like <see cref="Escape"/>, without citations or break points, and with maths given a plain
    /// form for the PDF's bookmarks, so hyperref has nothing to drop.
    /// </summary>
    public static string Heading(string input)
    {
        string escaped = Typography(Markup(Clean(input ?? "", ' ')).Replace("*", ""));
        return MathSpan.Replace(escaped, m => @"\texorpdfstring{" + m.Value + "}{" + Plain(m.Groups[1].Value) + "}");
    }

    /// <summary>Text for the PDF's metadata (hyperref's pdftitle, pdfkeywords): escaped, on one line, maths in plain words.</summary>
    public static string Metadata(string input)
    {
        string escaped = Markup(Regex.Replace(Clean(input ?? "", ' '), @"\s+", " ").Replace("*", ""));
        return MathSpan.Replace(escaped, m => Plain(m.Groups[1].Value)).Replace("``", "\"").Replace("''", "\"");
    }

    /// <summary>
    /// Escapes one line of the reference list. APA 7 ends a reference with a full stop, except after its DOI or address,
    /// where a full stop would read as part of the link; only that one is removed.
    /// </summary>
    public static string EscapeReference(string input)
    {
        string reference = Clean(input ?? "", ' ').TrimEnd(' ', ',');
        int lastSpace = reference.LastIndexOf(' ');
        if (reference.EndsWith('.') && reference[(lastSpace + 1)..].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            reference = reference.TrimEnd('.');
        return Escape(reference, ' ', references: true);
    }

    // \url reads its argument as characters, even inside a table or a caption, when "%" and "#" carry a backslash; a
    // brace must balance and a backslash would end the argument, so those three are written as their percent codes.
    private static string UrlText(string address) =>
        address.Replace(@"\", "%5C").Replace("{", "%7B").Replace("}", "%7D").Replace("%", @"\%").Replace("#", @"\#");

    // A web address is set aside before the text is escaped and comes back as \url{...}, a link that xurl may break
    // anywhere; the placeholder is plain letters and digits, which no step of the escaping changes.
    private static string HideAddresses(string text, List<string> addresses) => WebAddress.Replace(text, m =>
    {
        string address = m.Value.TrimEnd('.', ',', ';', ':', '"', '\'');
        if (address.EndsWith(')') && !address.Contains('(')) address = address[..^1];
        if (address.EndsWith(']') && !address.Contains('[')) address = address[..^1];
        addresses.Add(address);
        return $"zzurl{addresses.Count - 1}zz" + m.Value[address.Length..];
    });

    private static string ShowAddresses(string escaped, List<string> addresses) => addresses.Count == 0 ? escaped
        : AddressPlaceholder.Replace(escaped, m => @"\url{" + UrlText(addresses[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]) + "}");

    private static string Escape(string input, char trimEnd, bool references = false, bool prose = false)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        // In-text citations are set sorted and ranged ("[1–4]"); a reference holds none, but may hold brackets.
        if (!references) input = CitationMarkers.Tidy(input);
        var addresses = new List<string>();
        string escaped = Markup(HideAddresses(Clean(input, trimEnd), addresses));
        // Markdown-style *italics* (used for venues in the APA references) -> real LaTeX italics.
        escaped = Regex.Replace(escaped, @"\*([^*\n]+)\*", @"\textit{$1}");
        if (!references) escaped = Typography(escaped);
        // A long hash or name may end the line at its separators, so it stays inside the margin.
        escaped = LatexBreaks.Allow(escaped);
        if (!references) escaped = Marker.Replace(escaped, Cite);
        // One sentence per line in a paragraph; elsewhere a full stop before a command ("orchestration. \textit{...}")
        // is not a sentence's end, so it gets a word space.
        if (prose) return ShowAddresses(SentenceEnd.Replace(escaped, "\n"), addresses);
        return ElementStop.Replace(ShowAddresses(escaped, addresses), @".\ ");
    }

    private static string Clean(string input, char trimEnd) =>
        input.Replace("https://doi.org/XXXX-XXXXXX", "")
             .Replace("https://doi.org/XXXXXXX.XXXXXXX", "")
             .Replace("https://doi.org/XX.XXXX/", "")
             .Replace("https://doi.org/XXXX", "")
             .Replace("https://doi.org/XXX", "")
             .Replace("XXXXXX.XXXXX", "")
             .TrimEnd(trimEnd);

    // The characters LaTeX reads as commands, written as characters, and those pdflatex cannot set, mapped.
    private static string Markup(string text)
    {
        string escaped = text.Replace(@"\", @"\textbackslash ")
                             .Replace("&", @"\&")
                             .Replace("%", @"\%")
                             .Replace("$", @"\$")
                             .Replace("_", @"\_")
                             .Replace("#", @"\#")
                             .Replace("{", @"\{")
                             .Replace("}", @"\}")
                             .Replace("~", @"\textasciitilde ")
                             .Replace("^", @"\textasciicircum ");
        // A number raised to a number ("10^6", "10^-3") is set as a power, not as a caret (issue #129).
        escaped = Regex.Replace(escaped, @"(?<![\w.])(\d+(?:\.\d+)?)\\textasciicircum (-?\d+)(?!\w)", m => $"${m.Groups[1].Value}^{{{m.Groups[2].Value}}}$");
        // Straight double quotes print as two closing quotes in LaTeX; use proper ``opening'' and closing quotes.
        escaped = Regex.Replace(escaped, "\"([^\"\n]*)\"", "``$1''");
        // Characters pdflatex cannot set from UTF-8 with this preamble: Cohen's κ in a multivocal method, a Greek letter
        // in a source's name ("α-UMi", #475), an emoji in a repository's title.
        // The marks of unconfirmed citations (CitationMarkers) become a dagger, the first with its footnote.
        escaped = LatexText.Map(CitationMarkers.MarksToLatex(escaped));
        escaped = escaped.Replace("...", @"\ldots{}");
        // Two maths spans side by side ("2×10^6") become one, since "$$" would start display maths.
        return Regex.Replace(escaped, @"(?<!\\)\$\$", "");
    }

    private static string Typography(string escaped)
    {
        // An en dash in a range; a word with "/" or ":" is an address, a DOI or a time and keeps its hyphens.
        escaped = Regex.Replace(escaped, @"\S+", m => m.Value.Contains('/') || m.Value.Contains(':') ? m.Value : NumberRange.Replace(m.Value, "$1--$2"));
        // An ORCID iD is a name, not a range: "-{}" keeps its hyphens and tells chktex so.
        escaped = Orcid.Replace(escaped, "$1-{}$2-{}$3-{}$4");
        escaped = EtAl.Replace(escaped, @"et~al.\ ");
        escaped = Abbreviation.Replace(escaped, @"$1.\ ");
        escaped = AcronymStop.Replace(escaped, @"$1\@.");
        return LoneMark.Replace(escaped, "{?}");
    }

    // "[1, 3–5]" -> "~\cite{ref1,ref3,ref4,ref5}": the cite package sorts and ranges them again, as CitationMarkers does.
    private static string Cite(Match m)
    {
        var numbers = m.Groups[2].Value.Split(", ").SelectMany(part =>
        {
            var ends = part.Split('–').Select(int.Parse).ToArray();
            return ends.Length == 2 ? Enumerable.Range(ends[0], ends[1] - ends[0] + 1) : ends;
        });
        string cite = @"\cite{" + string.Join(",", numbers.Select(n => "ref" + n)) + "}";
        // A citation that starts a table cell or follows a bracket is braced, which tells chktex no space was wanted.
        return m.Groups[1].Length > 0 ? "~" + cite : "{" + cite + "}";
    }

    private static string Plain(string maths) => Regex.Replace(maths, @"\\([a-zA-Z]+)\s*", "$1 ").Replace("{", "").Replace("}", "").Replace("^", "").Replace("_", "").Trim();
}
