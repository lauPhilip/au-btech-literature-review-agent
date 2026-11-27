using System;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// The DOI of a gallery version, checked in code before it is shown or put in a citation. The value comes from
/// entry.json, which a person edits, so only the plain form of a DOI is accepted: "10." and a registrant number, a
/// slash, and a suffix of letters, digits and . _ - ; : ( ) /. Nothing that could close an HTML attribute, a JSON string
/// or a BibTeX field gets through.
/// </summary>
public static partial class GalleryDoi
{
    public const string Resolver = "https://doi.org/";

    [GeneratedRegex(@"^10\.\d{4,9}/[A-Za-z0-9._;:()/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Plain();

    /// <summary>The bare DOI ("10.5281/zenodo.123"), with a "doi:" or doi.org prefix removed; null when it is empty or not a DOI.</summary>
    public static string? Normalize(string? value)
    {
        string v = (value ?? "").Trim();
        foreach (string prefix in new[] { "https://doi.org/", "http://doi.org/", "https://dx.doi.org/", "doi:" })
        {
            if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { v = v[prefix.Length..].Trim(); break; }
        }
        return Plain().IsMatch(v) ? v : null;
    }

    /// <summary>The DOI's landing address ("https://doi.org/10.5281/zenodo.123"), or null without a valid DOI.</summary>
    public static string? Landing(string? value) => Normalize(value) is { } doi ? Resolver + doi : null;
}
