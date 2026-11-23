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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly Regex Tag = new(@"<(/?)([a-zA-Z][a-zA-Z0-9:\-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", Options, Timeout);
    private static readonly Regex Attribute = new(@"([^\s=/""'>]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?", Options, Timeout);

    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    /// <summary>
    /// The page's HTML without its hidden elements, how many were dropped, and their text. An element is hidden by the
    /// hidden attribute, aria-hidden="true", or an inline style that shows nothing (<see cref="StyleHides"/>). A
    /// stylesheet or a class is not read, so a class such as "hidden" is not seen. An element that is never closed is
    /// dropped up to the next closing tag, which is about where a browser would end it.
    /// </summary>
    public static CleanedText DropHiddenHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return new CleanedText("", 0, 0, "");
        var tags = Tag.Matches(html).ToList();
        var closes = MatchingCloses(tags);

        var kept = new StringBuilder(html.Length);
        var hidden = new StringBuilder();
        int copied = 0, elements = 0;
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (tag.Index < copied || tag.Groups[1].Length > 0 || Void.Contains(tag.Groups[2].Value) || !Hides(tag.Groups[3].Value)) continue;

            int end;
            if (tag.Groups[3].Value.TrimEnd().EndsWith('/')) end = tag.Index + tag.Length;
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

    private static bool Hides(string attributes)
    {
        foreach (Match a in Attribute.Matches(attributes))
        {
            string name = a.Groups[1].Value.ToLowerInvariant();
            string value = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
            if (name == "hidden") return true;
            if (name == "aria-hidden" && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (name == "style" && StyleHides(value)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether an inline style shows nothing: display none; visibility hidden; a font size or opacity of zero; a
    /// transparent text colour, or the same colour as the element's own background; placed 999 pixels or more off the
    /// screen; clipped to nothing; or no width or height with the overflow hidden.
    /// </summary>
    public static bool StyleHides(string style)
    {
        var d = new Dictionary<string, string>();
        foreach (var declaration in style.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            string value = declaration[(colon + 1)..].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
            d[declaration[..colon].Trim().ToLowerInvariant()] = value;
        }
        string Get(string name) => d.TryGetValue(name, out var v) ? v : "";

        if (Get("display") == "none" || Get("visibility") is "hidden" or "collapse") return true;
        if (Number(Get("font-size")) is <= 0 || Number(Get("opacity")) is < 0.1) return true;
        if (Get("transform").Replace(" ", "") is "scale(0)" or "scale(0,0)") return true;
        if (new[] { "left", "top", "right", "bottom", "text-indent", "margin-left", "margin-top" }.Any(p => Number(Get(p)) is <= -999)) return true;
        if (Get("clip").StartsWith("rect(", StringComparison.Ordinal) && Regex.Matches(Get("clip"), @"-?\d+(\.\d+)?").All(m => double.Parse(m.Value, CultureInfo.InvariantCulture) <= 1)) return true;
        if (Get("overflow") == "hidden" && new[] { "width", "height", "max-width", "max-height" }.Any(p => Number(Get(p)) is <= 0)) return true;

        var color = Color(Get("color"));
        if (color is { A: 0 }) return true;
        var background = Color(Get("background-color")) ?? Color(Get("background").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        return color is { } c && background is { A: > 0 } b
            && Math.Abs(c.R - b.R) <= 16 && Math.Abs(c.G - b.G) <= 16 && Math.Abs(c.B - b.B) <= 16;
    }

    /// <summary>The leading number of a CSS length, such as -9999 for "-9999px"; null when there is none.</summary>
    private static double? Number(string value)
    {
        var m = Regex.Match(value, @"^-?(\d+(\.\d*)?|\.\d+)", RegexOptions.None, Timeout);
        return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    private readonly record struct Rgba(int R, int G, int B, double A);

    /// <summary>A CSS colour written as a hex code, rgb()/rgba(), white, black or transparent; null for anything else.</summary>
    private static Rgba? Color(string? value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        switch (value)
        {
            case "white": return new Rgba(255, 255, 255, 1);
            case "black": return new Rgba(0, 0, 0, 1);
            case "transparent": return new Rgba(0, 0, 0, 0);
        }
        if (Regex.Match(value, @"^#([0-9a-f]{3,8})$", RegexOptions.None, Timeout) is { Success: true } hex)
        {
            string h = hex.Groups[1].Value;
            if (h.Length is 3 or 4) h = string.Concat(h.Select(ch => $"{ch}{ch}"));
            if (h.Length is not (6 or 8)) return null;
            int Channel(int at) => int.Parse(h.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Rgba(Channel(0), Channel(2), Channel(4), h.Length == 8 ? Channel(6) / 255.0 : 1);
        }
        if (Regex.Match(value, @"^rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)\s*(?:[,/]\s*([\d.]+)(%?))?\s*\)$", RegexOptions.None, Timeout) is { Success: true } rgb)
        {
            double alpha = rgb.Groups[4].Success ? double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) / (rgb.Groups[5].Value == "%" ? 100 : 1) : 1;
            return new Rgba(int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture), alpha);
        }
        return null;
    }
}
