using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>One block of a report section: a paragraph, a quotation, a list or a table.</summary>
public sealed class ReportBlock
{
    /// <summary>"paragraph", "quote", "list" or "table".</summary>
    public string Kind { get; init; } = "paragraph";
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Items { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = Array.Empty<IReadOnlyList<string>>();

    public static ReportBlock Paragraph(string text) => new() { Kind = "paragraph", Text = text };
    public static ReportBlock Quote(string text) => new() { Kind = "quote", Text = text };
    public static ReportBlock List(IEnumerable<string> items) => new() { Kind = "list", Items = items.ToList() };
    public static ReportBlock Table(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows) =>
        new() { Kind = "table", Headers = headers.ToList(), Rows = rows.Select(r => (IReadOnlyList<string>)r.ToList()).ToList() };
}

/// <summary>A section of the report, with its heading level (2 or 3).</summary>
public sealed class ReportSection
{
    public string Heading { get; init; } = "";
    public int Level { get; init; } = 2;
    public List<ReportBlock> Blocks { get; } = new();
}

/// <summary>
/// The report of a multivocal run (MLR block G, G14), as data: the page shows it and <see cref="ToMarkdown"/> writes it
/// to multivocal-report.md for the archive. Every sentence is written in code from the run's files, never by the
/// model; the only model text in it is the themes' names and descriptions, which say so, and every quotation is a
/// finding whose quote was found in the kept page text.
/// </summary>
public sealed class MultivocalReport
{
    public string Title { get; init; } = "";

    /// <summary>The lines under the title: run, dates, method, protocol fingerprint.</summary>
    public List<string> Meta { get; } = new();
    public List<ReportSection> Sections { get; } = new();

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Title}").AppendLine();
        foreach (var line in Meta) sb.AppendLine($"- {line}");
        sb.AppendLine();
        foreach (var section in Sections)
        {
            sb.AppendLine($"{new string('#', section.Level)} {section.Heading}").AppendLine();
            foreach (var block in section.Blocks)
            {
                switch (block.Kind)
                {
                    case "quote":
                        sb.AppendLine($"> {block.Text}");
                        break;
                    case "list":
                        foreach (var item in block.Items) sb.AppendLine($"- {item}");
                        break;
                    case "table":
                        sb.AppendLine($"| {string.Join(" | ", block.Headers.Select(Cell))} |");
                        sb.AppendLine($"|{string.Concat(block.Headers.Select(_ => "---|"))}");
                        foreach (var row in block.Rows) sb.AppendLine($"| {string.Join(" | ", row.Select(Cell))} |");
                        break;
                    default:
                        sb.AppendLine(block.Text);
                        break;
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>Everything the report is written from: the run's files, loaded.</summary>
public sealed record MultivocalReportInput(
    PlannedRun Planned,
    MultivocalLedger Ledger,
    MultivocalScreeningFile Screening,
    MultivocalPagesFile Pages,
    MultivocalQualityFile Quality,
    MultivocalMapFile Map,
    MultivocalExtractionFile Extraction,
    MultivocalSynthesisFile Synthesis);

internal static class ReportSectionExtensions
{
    public static ReportSection With(this ReportSection section, ReportBlock block)
    {
        section.Blocks.Add(block);
        return section;
    }
}
