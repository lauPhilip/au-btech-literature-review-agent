using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

// The data section, the theme notes, the source repository and the writer's evidence of a review with both pools
// (block I-6), in code: the flow diagram gets an arm for each pool, and an academic source that rests on its abstract
// alone is marked wherever it is listed.
public static partial class MultivocalFormalText
{
    /// <summary>The opening text of the data section, with both pools.</summary>
    public static string DataText(MultivocalReportInput input)
    {
        var (grey, formal) = Pools(input);
        return $"The grey searches found {Count(grey.Identified, "source")} and the academic databases{(formal.ViaSeeds > 0 ? $" and citation chaining from the most-cited included sources ({Num(formal.ViaSeeds)})" : "")} returned {Count(formal.Identified, "record")}, of which {Num(formal.DuplicatesRemoved + formal.NoAddressRemoved)} {Was(formal.DuplicatesRemoved + formal.NoAddressRemoved)} set aside before screening. " +
               $"{Num(grey.Screened)} grey and {Num(formal.Screened)} academic sources were screened, and {Num(grey.Included)} and {Num(formal.Included)} included; " +
               $"{Count(grey.Kept, "grey source")} passed the quality threshold of {input.Quality.Threshold} points and {Count(formal.Kept, "academic source")} {Was(formal.Kept)} appraised with MMAT, " +
               $"and {Num(grey.Extracted)} grey and {Num(formal.Extracted)} academic sources were extracted against the systematic map ({Num(formal.AbstractOnly)} academic on the abstract alone). " +
               "The figures below show the flow of sources in each pool, the extracted sources by kind and outlet tier, and the grey sources' quality scores; the table shows how many sources state each value of the map.";
    }

    /// <summary>The flow of sources with one arm per pool, from the search to the extraction, joined at the synthesis.</summary>
    public static string FlowTikz(MultivocalReportInput input)
    {
        var (g, f) = Pools(input);
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[node distance=0.5cm and 0.6cm, box/.style={draw, rectangle, align=center, font=\footnotesize, text width=3.6cm, minimum height=0.75cm}, side/.style={draw, rectangle, align=left, font=\scriptsize, text width=3.3cm, fill=black!3}, >={Stealth}]");
        sb.AppendLine($@"\node[box] (gfound) {{Grey literature found: {g.Identified}}};");
        sb.AppendLine($@"\node[box, below=of gfound] (gscreened) {{{MultivocalScreeningText.FlowBox(input.Screening, g.Screened, g.Second?.ScreenedTwice ?? 0)}}};");
        sb.AppendLine($@"\node[box, below=of gscreened] (gincluded) {{Included: {g.Included}}};");
        sb.AppendLine($@"\node[box, below=of gincluded] (gkept) {{Passed the quality threshold: {g.Kept}}};");
        sb.AppendLine($@"\node[box, below=of gkept] (gextracted) {{Extracted: {g.Extracted}}};");
        // Each side box sits beside the step its sources left at (#362): screening beside the screening box, quality beside the quality step.
        sb.AppendLine($@"\node[side, left=of gscreened] (gout) {{Same title: {g.DuplicatesAtScreening}\\Not screened: {g.NotScreened}\\Excluded: {g.Excluded}}};");
        sb.AppendLine($@"\node[side, left=of gkept] (glow) {{Below the threshold: {g.BelowThreshold}\\Not assessed: {g.NotAssessed}}};");
        string viaSeeds = f.ViaSeeds > 0 ? $@"\\{{\scriptsize of which by citation chaining from the most-cited: {f.ViaSeeds}}}" : "";
        sb.AppendLine($@"\node[box, right=1.2cm of gfound] (ffound) {{Academic records found: {f.Identified}{viaSeeds}}};");
        sb.AppendLine($@"\node[box, below=of ffound] (fscreened) {{{MultivocalScreeningText.FlowBox(input.Screening, f.Screened, f.Second?.ScreenedTwice ?? 0)}}};");
        sb.AppendLine($@"\node[box, below=of fscreened] (fincluded) {{Included: {f.Included}}};");
        sb.AppendLine($@"\node[box, below=of fincluded] (fkept) {{Appraised with MMAT: {f.Kept}}};");
        sb.AppendLine($@"\node[box, below=of fkept] (fextracted) {{Extracted: {f.Extracted}\\{{\scriptsize of which on the abstract only: {f.AbstractOnly}}}}};");
        sb.AppendLine($@"\node[side, right=of fscreened] (fout) {{Duplicates by DOI or title: {f.DuplicatesRemoved}\\No web address: {f.NoAddressRemoved}\\Same title: {f.DuplicatesAtScreening}\\Not screened: {f.NotScreened}\\Excluded: {f.Excluded}}};");
        sb.AppendLine($@"\node[side, right=of fkept] (fnotappraised) {{Not appraised: {f.NotAssessed}}};");
        // The sources kept but not extracted get a box in each arm, so each column adds up from step to step (#467).
        sb.AppendLine($@"\node[side, left=of gextracted] (gnotextracted) {{Could not be extracted: {g.NotExtracted}}};");
        sb.AppendLine($@"\node[side, right=of fextracted] (fnotextracted) {{Could not be extracted: {f.NotExtracted}}};");
        sb.AppendLine($@"\node[box, text width=8cm, below=0.8cm of gextracted.south east, anchor=north west, xshift=-1.8cm] (themes) {{Synthesised: {Count(input.Synthesis.Findings.Count, "finding")} in {Count(MultivocalPaperParts.ShownThemes(input).Count, "theme")}}};");
        foreach (string arm in new[] { "g", "f" })
            sb.AppendLine($@"\draw[->] ({arm}found) -- ({arm}screened); \draw[->] ({arm}screened) -- ({arm}included); \draw[->] ({arm}included) -- ({arm}kept); \draw[->] ({arm}kept) -- ({arm}extracted); \draw[->] ({arm}extracted) -- (themes);");
        sb.AppendLine(@"\draw[->] (gscreened) -- (gout); \draw[->] (fscreened) -- (fout); \draw[->] (gkept) -- (glow); \draw[->] (fkept) -- (fnotappraised);");
        sb.AppendLine(@"\draw[->] (gextracted) -- (gnotextracted); \draw[->] (fextracted) -- (fnotextracted);");
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    /// <summary>The pools a theme rests on, for the note under it: " (1 grey and 2 academic)", or empty for a grey-only theme.</summary>
    public static string ThemePools(SynthesisTheme theme) =>
        theme.FormalSources == 0 ? "" : $" ({Num(theme.GreySources)} grey and {Num(theme.FormalSources)} academic)";

    /// <summary>The writer's profile line of an academic source: what it is, how it was appraised, and what text it rests on.</summary>
    public static string Profile(GreyRecord record, GreyQuality? quality, PageSnapshot? page) =>
        $"Academic literature in {(record.Site.Length > 0 ? record.Site : "a bibliographic database")}; " +
        (quality?.Mmat is { } mmat ? $"MMAT: {MultivocalMapper.MmatSummary(mmat)}" : "not appraised") + "; " +
        (page?.TextBasis == MultivocalPages.AbstractBasis ? "read on its abstract only" : "read on its full text") + "; " +
        (record.Published is { } d ? $"published {d.Year}" : "no date") + ".";

    /// <summary>The facts about the formal pool for the discussion's limitations.</summary>
    public static IEnumerable<string> BaseFacts(MultivocalReportInput input)
    {
        var (_, f) = Pools(input);
        yield return $"{Count(f.Extracted, "academic source")} from the bibliographic databases, peer reviewed or preprints, appraised with MMAT; {Num(f.AbstractOnly)} of them were read on the abstract alone, as no open full text was found.";
        yield return "Grey and academic sources were screened, mapped and extracted with the same criteria; tensions between them show where the state of the art and the state of the practice differ.";
    }

    /// <summary>The kind, tier and quality cells of an academic source in the source repository; abstract only is marked.</summary>
    public static (string Kind, string Tier, string Quality) RepositoryCells(GreyQuality? quality, PageSnapshot? page) => (
        page?.TextBasis == MultivocalPages.AbstractBasis ? "Academic literature (abstract only)" : "Academic literature (full text)",
        "–",
        quality?.Mmat is { } mmat ? $"MMAT: {MultivocalMapper.MmatSummary(mmat)}" : "not appraised");

    private static (PoolCounts Grey, PoolCounts Formal) Pools(MultivocalReportInput input) => (
        MultivocalPools.For(false, input.Ledger, input.Screening, input.Quality, input.Extraction),
        MultivocalPools.For(true, input.Ledger, input.Screening, input.Quality, input.Extraction));
}
