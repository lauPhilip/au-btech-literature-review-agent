using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>Hidden parts of a web page (#369): what its attributes or its inline style hide from a reader.</summary>
public static partial class HiddenText
{
    private const RegexOptions Options = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    /// <summary>A property, not a field, so the regexes of every part of this partial class can use it whatever order their fields start in.</summary>
    private static TimeSpan Timeout => TimeSpan.FromSeconds(5);

    private static readonly Regex Tag = new(@"<(/?)([a-zA-Z][a-zA-Z0-9:\-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", Options, Timeout);
    private static readonly Regex Attribute = new(@"([^\s=/""'>]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?", Options, Timeout);

    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    /// <summary>
    /// The page's HTML without its hidden elements, how many were dropped, and their text. An element is hidden by the
    /// hidden attribute, aria-hidden="true", a hiding class (<see cref="ClassHides"/>), or a style that shows nothing
    /// (<see cref="StyleHides"/>): its inline style together with the rules of the page's own style blocks
    /// (<see cref="PageStyleSheet"/>, #535), and text in the colour of the background it is drawn on, set on the
    /// element or on one around it (#536). External stylesheets are not fetched. An element that is never closed is
    /// dropped up to the next closing tag, which is about where a browser would end it.
    /// </summary>
    /// <param name="sheet">The page's style rules; read from <paramref name="html"/> when not given.</param>
    public static CleanedText DropHiddenHtml(string? html, PageStyleSheet? sheet = null)
    {
        if (string.IsNullOrEmpty(html)) return new CleanedText("", 0, 0, "");
        sheet ??= PageStyleSheet.Of(html);
        var tags = Tag.Matches(html).ToList();
        var closes = MatchingCloses(tags);

        var kept = new StringBuilder(html.Length);
        var hidden = new StringBuilder();
        var open = new List<HtmlElement>();
        int copied = 0, elements = 0;
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            string name = tag.Groups[2].Value.ToLowerInvariant();
            if (tag.Index < copied || Void.Contains(name)) continue;
            if (tag.Groups[1].Length > 0)
            {
                int at = open.FindLastIndex(e => e.Name == name);
                if (at >= 0) open.RemoveRange(at, open.Count - at);
                continue;
            }
            if (ClosesItself.Contains(name) && open.Count > 0 && open[^1].Name == name) open.RemoveAt(open.Count - 1);

            var element = Element(name, tag.Groups[3].Value, out bool hiddenByAttribute);
            element.Computed = sheet.Cascade(element, open);
            bool selfClosing = tag.Groups[3].Value.TrimEnd().EndsWith('/');
            if (Structure.Contains(name) || !(hiddenByAttribute || ClassHides(element.Classes) || StyleHides(element.Computed) || SameAsBackground(element, open)))
            {
                if (!selfClosing) open.Add(element);
                continue;
            }

            int end;
            if (selfClosing) end = tag.Index + tag.Length;
            else if (closes[i] is int close) end = tags[close].Index + tags[close].Length;
            else
            {
                int next = html.IndexOf("</", tag.Index + tag.Length, StringComparison.Ordinal);
                end = next < 0 ? html.Length : next;
            }
            kept.Append(html, copied, tag.Index - copied).Append(' ');
            hidden.Append(TextOf(html[tag.Index..end])).Append('\n');
            copied = end;
            elements++;
        }
        kept.Append(html, copied, html.Length - copied);
        return new CleanedText(kept.ToString(), 0, elements, hidden.ToString());
    }

    /// <summary>The page itself is never dropped as a whole, whatever its style says.</summary>
    private static readonly HashSet<string> Structure = new(StringComparer.Ordinal) { "html", "head", "body" };

    /// <summary>Elements a browser closes when the same one opens again, so they do not pile up as ancestors.</summary>
    private static readonly HashSet<string> ClosesItself = new(StringComparer.Ordinal) { "p", "li", "dt", "dd", "tr", "td", "th", "option" };

    /// <summary>For every opening tag, the index of its closing tag of the same name, matched by nesting; one pass.</summary>
    private static int?[] MatchingCloses(List<Match> tags)
    {
        var closes = new int?[tags.Count];
        var open = new Dictionary<string, Stack<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tags.Count; i++)
        {
            string name = tags[i].Groups[2].Value;
            if (Void.Contains(name)) continue;
            if (!open.TryGetValue(name, out var stack)) open[name] = stack = new Stack<int>();
            if (tags[i].Groups[1].Length > 0) { if (stack.Count > 0) closes[stack.Pop()] = i; }
            else if (!tags[i].Groups[3].Value.TrimEnd().EndsWith('/')) stack.Push(i);
        }
        return closes;
    }

    /// <summary>The readable text of an HTML fragment, on one line.</summary>
    public static string TextOf(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ", Options, Timeout)), @"\s+", " ", RegexOptions.None, Timeout).Trim();

    private static HtmlElement Element(string name, string attributes, out bool hidden)
    {
        hidden = false;
        string? id = null, style = "";
        string[] classes = Array.Empty<string>();
        foreach (Match a in Attribute.Matches(attributes))
        {
            string attribute = a.Groups[1].Value.ToLowerInvariant();
            string value = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
            if (attribute == "hidden") hidden = true;
            else if (attribute == "aria-hidden" && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) hidden = true;
            else if (attribute == "style") style = value;
            else if (attribute == "id") id = value.Trim();
            else if (attribute == "class") classes = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }
        return new HtmlElement(name, id, classes, style);
    }

    /// <summary>
    /// Whether an inline style shows nothing: display none; visibility hidden; a font size or opacity of zero; a
    /// transparent text colour, or the same colour as the element's own background; placed 999 pixels or more off the
    /// screen; clipped to nothing; or no width or height with the overflow hidden.
    /// </summary>
    public static bool StyleHides(string style)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value, _) in PageStyleSheet.Declarations(style)) d[name] = value;
        return StyleHides(d);
    }

    /// <summary>Whether a style, as a dictionary of declarations after the cascade, shows nothing; see <see cref="StyleHides(string)"/>.</summary>
    public static bool StyleHides(IReadOnlyDictionary<string, string> d)
    {
        string Get(string name) => d.TryGetValue(name, out var v) ? v : "";

        if (Get("display") == "none" || Get("visibility") is "hidden" or "collapse") return true;
        if (Number(Get("font-size")) is <= 0 || Number(Get("opacity")) is < 0.1) return true;
        if (Get("transform").Replace(" ", "") is "scale(0)" or "scale(0,0)") return true;
        if (new[] { "left", "top", "right", "bottom", "text-indent", "margin-left", "margin-top" }.Any(p => Number(Get(p)) is <= -999)) return true;
        if (Get("clip").StartsWith("rect(", StringComparison.Ordinal) && Regex.Matches(Get("clip"), @"-?\d+(\.\d+)?").All(m => double.Parse(m.Value, CultureInfo.InvariantCulture) <= 1)) return true;
        if (Get("overflow") == "hidden" && new[] { "width", "height", "max-width", "max-height" }.Any(p => Number(Get(p)) is <= 0)) return true;

        var color = Color(Get("color"));
        if (color is { A: 0 }) return true;
        return color is { } c && Background(d) is { Declared: true, Colour: { } b } && Near(c, b);
    }
}
