using System.Globalization;

namespace AuBtechReviewAgent;

/// <summary>
/// Makes every page format numbers and dates the same way as the paper, whatever culture the server runs in.
/// The live server is Danish, so a page that formatted a number with the server's culture showed "kappa 0,63"
/// while the paper said 0.63 (issue #134). Called once, first thing in Program.cs.
/// </summary>
public static class SiteCulture
{
    public static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
