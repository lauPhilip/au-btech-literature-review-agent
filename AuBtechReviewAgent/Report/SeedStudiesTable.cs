using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// The appendix table of the seed studies of citation chaining, written in code from the seed record on file, for the
/// main.tex of every kind of review that has a seed step: each seed's title, year, citations, citations per year and rank,
/// and how many neighbours it gave for screening.
/// </summary>
public static class SeedStudiesTable
{
    /// <summary>The table as LaTeX, with the label <paramref name="label"/> (for example "tab:A.4"); empty when there were no seeds.</summary>
    public static string Latex(SeedChainingRecord? record, string label, Func<string, string> escape)
    {
        if (record == null || record.Seeds.Count == 0) return "";
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tabularx}{\textwidth}{r Y c r r c}");
        sb.Append(PaperLatex.TableHead("Seed studies for citation chaining", label, @"\textbf{Rank} & \textbf{Title} & \textbf{Year} & \textbf{Citations} & \textbf{Per year} & \textbf{Kept}"));
        foreach (var seed in record.Seeds.OrderBy(s => s.Rank))
        {
            string kept = seed.Error != null ? "--" : string.Create(inv, $"{seed.Backward} / {seed.Forward}");
            sb.AppendLine(string.Create(inv, $"{seed.Rank} & {escape(seed.Title)} & {seed.Year} & {seed.CitedByCount} & {seed.CitationsPerYear:0.0} & {kept} \\\\ \\addlinespace"));
        }
        sb.AppendLine(@"\bottomrule");
        sb.AppendLine(@"\end{tabularx}");
        string note = string.Create(inv,
            $"The seeds were chosen in code, not by a language model: the included studies with the most citations per year up to {record.ReferenceYear}, the publication year counting as one. ")
            + "Citations are the database's count on the day of the search. Kept is the number of works the seed cites / of works citing it that were taken forward to the duplicate check and screening"
            + (record.Seeds.Any(s => s.Error != null) ? "; a dash marks a failed look-up." : ".");
        sb.AppendLine(@"\vspace{2pt}\noindent{\scriptsize " + escape(note) + @"\par}");
        sb.AppendLine(@"\vspace{10pt}");
        return sb.ToString();
    }
}
