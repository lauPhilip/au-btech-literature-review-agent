using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Tokens;

namespace AuBtechReviewAgent;

/// <summary>A PDF page's text as a reader sees it, how many hidden runs of letters were taken out, and their text.</summary>
public sealed record PdfPageText(string Text, int HiddenParts, string Hidden);

/// <summary>
/// Takes the letters a reader of a PDF never sees out of its text (#534), as <see cref="HiddenText.DropHiddenHtml"/> does
/// for a web page. A letter is hidden when it is drawn in the invisible render mode (3 or 7), when its size is below
/// <see cref="MinPointSize"/>, when it lies wholly outside the page's crop box, when it is in an optional content layer
/// that is off when the document opens, or when it is white or nearly so and no box, figure or image is drawn under it.
/// Ordinary text stays: grey headers, superscripts and footnote markers are above the size limit and not white, white
/// text in a dark box or on a figure has something drawn under it, and the invisible text layer of a scanned page is kept
/// when images cover at least half the page, since that layer is the page's only text. A page with no hidden letter
/// keeps exactly the text PdfPig gives it, so nothing changes for a paper without tricks.
/// </summary>
public static class PdfVisibleText
{
    /// <summary>Letters smaller than this many points cannot be read; the smallest real superscripts are about 4 pt.</summary>
    public const double MinPointSize = 1.0;

    /// <summary>A colour channel at or above this (0 to 1) counts as white.</summary>
    public const double White = 0.94;

    /// <summary>Images that cover this share of the page or more make it a scan, whose invisible text layer is kept.</summary>
    public const double ScanCover = 0.5;

    /// <summary>The visible text of every page, with the names of the layers that are off read once from the document.</summary>
    public static IEnumerable<(int Number, PdfPageText Text)> Pages(PdfDocument document)
    {
        var off = OffLayers(document);
        foreach (var page in document.GetPages()) yield return (page.Number, Read(page, off));
    }

    /// <summary>The page's text without the letters a reader cannot see.</summary>
    public static PdfPageText Read(Page page, IReadOnlySet<string>? offLayers = null)
    {
        var letters = page.Letters;
        if (letters.Count == 0) return new PdfPageText(page.Text ?? "", 0, "");

        var inOffLayer = LettersInOffLayers(page, offLayers);
        var crop = page.CropBox.Bounds;
        bool scan = ImageCover(page, crop) >= ScanCover;
        var backdrops = Backdrops(page);

        var hiddenAt = new bool[letters.Count];
        bool any = false;
        for (int i = 0; i < letters.Count; i++)
        {
            var l = letters[i];
            if (string.IsNullOrWhiteSpace(l.Value)) continue;
            hiddenAt[i] = inOffLayer.Contains(Key(l))
                || (l.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip && !scan)
                || l.PointSize < MinPointSize
                || Outside(l.GlyphRectangle, crop)
                || (IsWhite(Drawn(l)) && !backdrops.Any(b => Inside(Centre(l.GlyphRectangle), b)));
            any |= hiddenAt[i];
        }
        if (!any) return new PdfPageText(page.Text ?? "", 0, "");

        var kept = new StringBuilder(letters.Count);
        var hidden = new StringBuilder();
        int parts = 0;
        bool inRun = false;
        for (int i = 0; i < letters.Count; i++)
        {
            string value = letters[i].Value;
            if (hiddenAt[i])
            {
                if (!inRun) { parts++; inRun = true; if (hidden.Length > 0) hidden.Append('\n'); }
                hidden.Append(value);
            }
            else if (string.IsNullOrWhiteSpace(value) && inRun) hidden.Append(value); // a space inside a hidden run is part of it
            else { kept.Append(value); inRun = false; }
        }
        return new PdfPageText(kept.ToString(), parts, hidden.ToString());
    }

    /// <summary>
    /// The names of the optional content layers that are off when the document opens: those in the default
    /// configuration's OFF list, and with BaseState OFF every layer not in its ON list.
    /// </summary>
    public static IReadOnlySet<string> OffLayers(PdfDocument document)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var scanner = document.Structure.TokenScanner;
            T? Get<T>(DictionaryToken? d, string key) where T : class, IToken =>
                d != null && d.TryGet(NameToken.Create(key), out var t) ? Resolve(t, scanner) as T : null;

            var properties = Get<DictionaryToken>(document.Structure.Catalog.CatalogDictionary, "OCProperties");
            var config = Get<DictionaryToken>(properties, "D");
            if (config == null) return names;
            IEnumerable<string> Names(ArrayToken? array) => array?.Data
                .Select(t => Resolve(t, scanner) as DictionaryToken)
                .Select(d => Get<StringToken>(d, "Name")?.Data)
                .OfType<string>() ?? Enumerable.Empty<string>();

            names.UnionWith(Names(Get<ArrayToken>(config, "OFF")));
            if (Get<NameToken>(config, "BaseState")?.Data == "OFF")
            {
                var on = Names(Get<ArrayToken>(config, "ON")).ToHashSet(StringComparer.Ordinal);
                names.UnionWith(Names(Get<ArrayToken>(properties, "OCGs")).Where(n => !on.Contains(n)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A damaged layer list hides nothing; the page text is still read.
        }
        return names;
    }

    private static IToken? Resolve(IToken? token, UglyToad.PdfPig.Tokenization.Scanner.IPdfTokenScanner scanner)
    {
        for (int hops = 0; token is IndirectReferenceToken r && hops < 8; hops++) token = scanner.Get(r.Data)?.Data;
        return token;
    }

    private static HashSet<(int, double, double, string)> LettersInOffLayers(Page page, IReadOnlySet<string>? offLayers)
    {
        var keys = new HashSet<(int, double, double, string)>();
        if (offLayers == null || offLayers.Count == 0) return keys;
        try
        {
            foreach (var (name, groups) in page.GetOptionalContents())
                foreach (var group in groups)
                    if (offLayers.Contains(group.Name ?? name)) AddLetters(group.MarkedContent, keys);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Optional content PdfPig cannot read is treated as shown.
        }
        return keys;
    }

    private static void AddLetters(MarkedContentElement element, HashSet<(int, double, double, string)> keys)
    {
        foreach (var l in element.Letters) keys.Add(Key(l));
        foreach (var child in element.Children) AddLetters(child, keys);
    }

    private static (int, double, double, string) Key(Letter l) =>
        (l.TextSequence, Math.Round(l.StartBaseLine.X, 2), Math.Round(l.StartBaseLine.Y, 2), l.Value);

    private static IColor? Drawn(Letter l) =>
        l.RenderingMode is TextRenderingMode.Stroke or TextRenderingMode.StrokeClip ? l.StrokeColor : l.FillColor;

    private static bool IsWhite(IColor? colour)
    {
        if (colour == null) return false;
        try
        {
            var (r, g, b) = colour.ToRGBValues();
            return r >= White && g >= White && b >= White;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // a pattern or an unusual colour space: not known to be white
        }
    }

    /// <summary>Filled shapes that are not white themselves, and images: what white text can be drawn on.</summary>
    private static List<PdfRectangle> Backdrops(Page page)
    {
        var list = new List<PdfRectangle>();
        try
        {
            foreach (var path in page.Paths)
                if (path.IsFilled && !path.IsClipping && !IsWhite(path.FillColor) && path.GetBoundingRectangle() is { } r) list.Add(r);
            list.AddRange(page.GetImages().Select(i => i.Bounds));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            list.Add(page.CropBox.Bounds); // the page's drawing could not be read: assume white text has something under it
        }
        return list;
    }

    private static double ImageCover(Page page, PdfRectangle crop)
    {
        double area = crop.Width * crop.Height;
        if (area <= 0) return 0;
        try
        {
            return page.GetImages().Sum(i => Overlap(i.Bounds, crop)) / area;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }

    private static double Overlap(PdfRectangle a, PdfRectangle b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left), h = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>Wholly outside the crop box, with a point of tolerance for letters that touch its edge.</summary>
    private static bool Outside(PdfRectangle glyph, PdfRectangle crop) =>
        glyph.Right < crop.Left - 1 || glyph.Left > crop.Right + 1 || glyph.Top < crop.Bottom - 1 || glyph.Bottom > crop.Top + 1;

    private static bool Inside(PdfPoint p, PdfRectangle r) => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top;

    private static PdfPoint Centre(PdfRectangle r) => new((r.Left + r.Right) / 2, (r.Bottom + r.Top) / 2);
}
