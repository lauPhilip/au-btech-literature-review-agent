using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The simple rules of a page's own style blocks (#535), so a class or an id that hides text is seen as well as an
/// inline style. Only what can hide text is kept: display, visibility, size, opacity, position, clipping, colour and
/// background. A selector is read when it is made of element names, classes and ids, joined by spaces (descendants);
/// anything else (pseudo-classes, attributes, child and sibling combinators) is skipped, and so is every at-rule block
/// (@media, @supports), since a rule there may not apply on a screen. External stylesheets are never fetched.
/// </summary>
public sealed class PageStyleSheet
{
    private const RegexOptions Options = RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>More rules than this are not read: a page's own style block is never this long unless it inlines a framework.</summary>
    public const int MaxRules = 20_000;

    private static readonly Regex StyleBlock = new(@"<style\b([^>]*)>(.*?)</style\s*>", Options, Timeout);
    private static readonly Regex Compound = new(@"^([a-z][a-z0-9-]*)?((?:[#.][a-z_-][\w-]*)*)$", Options, Timeout);
    private static readonly string[] Relevant = { "display", "visibility", "font-size", "opacity", "transform", "left", "top", "right", "bottom",
        "text-indent", "margin-left", "margin-top", "clip", "overflow", "width", "height", "max-width", "max-height", "color", "background" };

    /// <summary>One simple selector: an element name, an id and classes, each optional.</summary>
    private sealed record Part(string? Element, string? Id, string[] Classes);

    /// <summary>A rule: its selector from the outermost part to the element itself, its weight and place, and its declarations.</summary>
    private sealed record Rule(Part[] Parts, int Specificity, int Order, List<(string Name, string Value, bool Important)> Declarations);

    private readonly Dictionary<string, List<Rule>> _byKey = new(StringComparer.Ordinal);

    public static readonly PageStyleSheet Empty = new();

    /// <summary>How many rules were read.</summary>
    public int Count { get; private set; }

    /// <summary>The rules of every style block in the page, except blocks for print or another medium than the screen.</summary>
    public static PageStyleSheet Of(string? html)
    {
        var sheet = new PageStyleSheet();
        if (string.IsNullOrEmpty(html) || html.IndexOf("<style", StringComparison.OrdinalIgnoreCase) < 0) return sheet;
        foreach (Match block in StyleBlock.Matches(html))
        {
            var media = Regex.Match(block.Groups[1].Value, @"\bmedia\s*=\s*[""']?([^""'>]*)", Options, Timeout);
            if (media.Success && !Regex.IsMatch(media.Groups[1].Value.Trim(), @"^(all|screen)?$", Options, Timeout)) continue;
            sheet.Read(Regex.Replace(block.Groups[2].Value, @"/\*.*?\*/", " ", Options, Timeout));
        }
        return sheet;
    }

    private void Read(string css)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < css.Length && Count < MaxRules; i++)
        {
            char c = css[i];
            if (c == '}') { start = i + 1; continue; }
            if (c == '@' && depth == 0)
            {
                // Skip the whole at-rule: up to its ';' when it has no block, else to the end of its block.
                int semicolon = css.IndexOf(';', i), brace = css.IndexOf('{', i);
                if (brace < 0 || (semicolon >= 0 && semicolon < brace)) { i = semicolon < 0 ? css.Length : semicolon; start = i + 1; continue; }
                int level = 0, j = brace;
                for (; j < css.Length; j++) { if (css[j] == '{') level++; else if (css[j] == '}' && --level == 0) break; }
                i = j;
                start = i + 1;
                continue;
            }
            if (c == '{' && depth++ == 0)
            {
                int end = css.IndexOf('}', i);
                if (end < 0) return;
                Add(css[start..i], css[(i + 1)..end]);
                i = end;
                depth = 0;
                start = i + 1;
            }
        }
    }

    private void Add(string selectors, string block)
    {
        var declarations = Declarations(block).Where(d => Relevant.Any(r => d.Name.StartsWith(r, StringComparison.Ordinal))).ToList();
        if (declarations.Count == 0) return;
        foreach (string selector in selectors.Split(','))
        {
            var parts = new List<Part>();
            foreach (string token in selector.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Compound.Match(token);
                if (!m.Success || token.Length == 0) { parts.Clear(); break; }
                string? id = null;
                var classes = new List<string>();
                foreach (Match piece in Regex.Matches(m.Groups[2].Value, @"[#.][\w-]+", RegexOptions.None, Timeout))
                    if (piece.Value[0] == '#') id = piece.Value[1..]; else classes.Add(piece.Value[1..]);
                parts.Add(new Part(m.Groups[1].Success ? m.Groups[1].Value.ToLowerInvariant() : null, id, classes.ToArray()));
            }
            if (parts.Count == 0) continue;
            int specificity = parts.Sum(p => (p.Id != null ? 10_000 : 0) + p.Classes.Length * 100 + (p.Element != null ? 1 : 0));
            var rule = new Rule(parts.ToArray(), specificity, Count++, declarations);
            var last = rule.Parts[^1];
            string key = last.Id != null ? "#" + last.Id : last.Classes.Length > 0 ? "." + last.Classes[0] : last.Element!;
            if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = new List<Rule>();
            list.Add(rule);
        }
    }

    /// <summary>The declarations of a style attribute or a rule's block, in order, with their !important mark.</summary>
    public static List<(string Name, string Value, bool Important)> Declarations(string? style)
    {
        var list = new List<(string, string, bool)>();
        foreach (var declaration in (style ?? "").Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            string value = declaration[(colon + 1)..].Trim().ToLowerInvariant();
            bool important = value.Contains("!important", StringComparison.Ordinal);
            list.Add((declaration[..colon].Trim().ToLowerInvariant(), value.Replace("!important", "", StringComparison.Ordinal).Trim(), important));
        }
        return list;
    }

    /// <summary>
    /// The style of an element after the cascade: matching rules by weight and place, then the inline style, then the
    /// !important rules and the !important inline declarations. <paramref name="ancestors"/> runs from the outermost in.
    /// </summary>
    public Dictionary<string, string> Cascade(HtmlElement element, IReadOnlyList<HtmlElement> ancestors)
    {
        var inline = Declarations(element.Style);
        var matched = new List<Rule>();
        if (Count > 0)
        {
            var keys = element.Classes.Select(c => "." + c).Append(element.Name);
            if (element.Id != null) keys = keys.Append("#" + element.Id);
            foreach (string key in keys.Distinct())
                if (_byKey.TryGetValue(key, out var rules)) matched.AddRange(rules.Where(r => Matches(r, element, ancestors)));
        }
        var ordered = matched.Distinct().OrderBy(r => r.Specificity).ThenBy(r => r.Order).ToList();
        var style = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value, _) in ordered.SelectMany(r => r.Declarations).Where(d => !d.Important)) style[name] = value;
        foreach (var (name, value, _) in inline.Where(d => !d.Important)) style[name] = value;
        foreach (var (name, value, _) in ordered.SelectMany(r => r.Declarations).Where(d => d.Important)) style[name] = value;
        foreach (var (name, value, _) in inline.Where(d => d.Important)) style[name] = value;
        return style;
    }

    private static bool Matches(Rule rule, HtmlElement element, IReadOnlyList<HtmlElement> ancestors)
    {
        if (!Fits(rule.Parts[^1], element)) return false;
        int at = ancestors.Count - 1;
        for (int p = rule.Parts.Length - 2; p >= 0; p--)
        {
            while (at >= 0 && !Fits(rule.Parts[p], ancestors[at])) at--;
            if (at-- < 0) return false;
        }
        return true;
    }

    private static bool Fits(Part part, HtmlElement element) =>
        (part.Element == null || part.Element == element.Name)
        && (part.Id == null || part.Id == element.Id)
        && part.Classes.All(element.Classes.Contains);
}

/// <summary>An opening tag as the hidden-text check sees it, with its style after the cascade once worked out.</summary>
public sealed record HtmlElement(string Name, string? Id, string[] Classes, string Style)
{
    public Dictionary<string, string> Computed { get; set; } = new(StringComparer.Ordinal);
}
