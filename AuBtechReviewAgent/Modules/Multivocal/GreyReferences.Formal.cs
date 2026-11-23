using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// A formal source's reference (block I-6), cited as the systematic review cites a study: its APA line from
/// <see cref="ApaCitationBuilder"/>, and its BibTeX and RIS records from <see cref="BibliographyExporter"/>, numbered as
/// in the source repository. A source that rests on its abstract alone says so in each of them (decision 8).
/// </summary>
public static partial class GreyReferences
{
    public const string AbstractOnlyNote = "abstract only: no open full text was found, so the review read the abstract";

    private static Dictionary<string, AcademicPaper> Papers(MultivocalLedger ledger) =>
        ledger.FormalPool.GroupBy(s => MultivocalSearcher.AddressKey(s.Record.Url)).ToDictionary(g => g.Key, g => g.First().Paper!);

    /// <summary>Whether a reference rests on its abstract alone.</summary>
    public static bool AbstractOnly(GreyReference r) => r.Paper != null && r.TextBasis == MultivocalPages.AbstractBasis;

    private static string FormalLine(GreyReference r) =>
        ApaCitationBuilder.Build(r.Paper!).Citation + (AbstractOnly(r) ? $" ({AbstractOnlyNote})" : "");

    /// <summary>The source as the systematic review's record of an included study, for its exporters.</summary>
    private static IncludedPaperMetricRow Row(AcademicPaper paper, int number, string url)
    {
        var apa = ApaCitationBuilder.Build(paper);
        return new IncludedPaperMetricRow
        {
            Title = paper.Title ?? "",
            ApaCitation = apa.Citation,
            Year = apa.Year,
            VenueType = apa.VenueType,
            VenueName = apa.VenueName,
            Category = PrismaReviewEngine.SourceOfPaper(paper.Id).Category,
            ReferenceNumber = number,
            PaperId = paper.Id,
            Authors = paper.Authors ?? new List<string>(),
            Doi = PrismaReviewEngine.NormalizeDoi(paper.Doi),
            Url = url,
        };
    }

    private static string FormalBibTeX(GreyReference r, HashSet<string> used)
    {
        string entry = BibliographyExporter.ToBibTeX(new[] { Row(r.Paper!, r.Number, r.Url) });
        // One key space for the grey and the formal entries of the file.
        var key = Regex.Match(entry, @"^@\w+\{([^,]+),");
        if (key.Success) entry = entry.Remove(key.Groups[1].Index, key.Groups[1].Length).Insert(key.Groups[1].Index, UniqueKey(key.Groups[1].Value, used));
        return AbstractOnly(r) ? entry.Replace($"reference [{r.Number}]}}", $"reference [{r.Number}]; {AbstractOnlyNote}}}") : entry;
    }

    private static string FormalRis(GreyReference r)
    {
        string record = BibliographyExporter.ToRis(new[] { Row(r.Paper!, r.Number, r.Url) });
        string marks = (AbstractOnly(r) ? $"N1  - {Capital(AbstractOnlyNote)}{Environment.NewLine}" : "") + $"KW  - formal literature{Environment.NewLine}";
        int at = record.IndexOf("KW  - TraceableAI", StringComparison.Ordinal);
        return at < 0 ? record : record.Insert(at, marks);
    }

    /// <summary>A screened formal source: the systematic review's record of it, then the same decision tags as a grey source.</summary>
    private static string FormalScreenedRis(GreyScreening s, AcademicPaper paper, IReadOnlyDictionary<string, int>? numbers)
    {
        string record = BibliographyExporter.ToRis(new[] { Row(paper, 0, PrismaReviewEngine.IsWebAddress(s.Url) ? s.Url : "") });
        int end = record.IndexOf("N1  - Reference [", StringComparison.Ordinal);
        var sb = new StringBuilder(end < 0 ? record : record[..end]);
        sb.AppendLine("KW  - TraceableAI");
        sb.AppendLine("KW  - formal literature");
        AppendDecision(sb, s, numbers);
        return sb.ToString();
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
