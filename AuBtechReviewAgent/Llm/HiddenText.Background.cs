using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Hiding classes, and text in the colour of the background it is drawn on (#535, #536). The background is the one set
/// on the element or on the nearest element around it, up to body and html. No background is assumed where none is
/// set, since an external stylesheet (which is never fetched) can give the page a dark theme; an image, a gradient or
/// a CSS variable makes the background unknown, and unknown keeps the text.
/// </summary>
public static partial class HiddenText
{
    /// <summary>
    /// Classes that hide an element in the common frameworks (Bootstrap, Tailwind, WordPress). Text only for screen
    /// readers (sr-only, visually-hidden, screen-reader-text) counts as hidden too: the kept text is what a reviewer sees
    /// and can find again on the page, the inline clip that such a class sets was already read as hidden, and the scan
    /// still reads it, where only a strong phrase raises a flag, so an ordinary label never does.
    /// </summary>
    private static readonly HashSet<string> HidingClasses = new(StringComparer.Ordinal)
        { "d-none", "hidden", "invisible", "sr-only", "visually-hidden", "screen-reader-text" };

    /// <summary>A responsive or state class that shows the element again, such as md:block, lg:visible, not-sr-only or d-md-flex.</summary>
    private static readonly Regex Reshown = new(
        @"^(?:[\w\[\]\-]+:)+(?:block|inline|inline-block|flex|inline-flex|grid|inline-grid|table|table-row|table-cell|contents|flow-root|list-item|visible|not-sr-only)$" +
        @"|^d-(?:sm|md|lg|xl|xxl)-(?!none$)[a-z-]+$", RegexOptions.CultureInvariant, Timeout);

    /// <summary>Whether the classes hide the element: a hiding class, and no class that shows it again at some screen size or state.</summary>
    public static bool ClassHides(IReadOnlyCollection<string> classes) =>
        classes.Any(HidingClasses.Contains) && !classes.Any(Reshown.IsMatch);

    /// <summary>
    /// Whether an element that sets a text colour or a background draws its text in the colour of its background,
    /// taking the colour and the background from the nearest element that sets them (<paramref name="open"/> runs from
    /// the outermost in).
    /// </summary>
    private static bool SameAsBackground(HtmlElement element, List<HtmlElement> open)
    {
        if (!element.Computed.ContainsKey("color") && !Background(element.Computed).Declared) return false;
        var chain = open.Append(element).Reverse().ToList();

        Rgba? colour = null;
        foreach (var e in chain)
        {
            if (!e.Computed.TryGetValue("color", out var value) || value is "" or "inherit") continue;
            colour = Color(value);
            break;
        }
        if (colour is not { A: > 0 } c) return false;
        foreach (var e in chain)
        {
            var background = Background(e.Computed);
            if (background.Declared) return background.Colour is { } b && Near(c, b);
        }
        return false;
    }

    private static bool Near(Rgba c, Rgba b) => Math.Abs(c.R - b.R) <= 16 && Math.Abs(c.G - b.G) <= 16 && Math.Abs(c.B - b.B) <= 16;

    private static readonly Regex ColourToken = new(@"rgba?\([^)]*\)|#[0-9a-f]{3,8}\b|\b(?:white|black|transparent)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    /// <summary>
    /// The background a style sets: not declared (none, transparent, inherit), or declared with its colour, or declared
    /// but unknown (an image, a gradient, a variable or a colour name not read here), which is a null colour.
    /// </summary>
    private static (bool Declared, Rgba? Colour) Background(IReadOnlyDictionary<string, string> d)
    {
        static bool Empty(string v) => v is "" or "none" or "transparent" or "inherit" or "initial" or "unset";
        string image = d.TryGetValue("background-image", out var i) ? i : "";
        if (!Empty(image)) return (true, null);
        foreach (string property in new[] { "background-color", "background" })
        {
            if (!d.TryGetValue(property, out var value) || Empty(value)) continue;
            if (value.Contains("url(", StringComparison.Ordinal) || value.Contains("gradient(", StringComparison.Ordinal)) return (true, null);
            var token = ColourToken.Match(value);
            var colour = token.Success ? Color(token.Value) : null;
            if (colour is { A: 0 }) continue;
            return (true, colour);
        }
        return (false, null);
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
