using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AuBtechReviewAgent;

// The counts of the answers in code split by pool (issue #459): a frequency question such as "how often in grey and
// in academic sources?" asks for the comparison the guidelines draw between the state of the practice and the state of
// the art, and each finding is already labelled grey or academic in code.
public static partial class MultivocalPaperParts
{
    /// <summary>How many extracted sources each pool has, and the reference numbers of the academic ones.</summary>
    public sealed record PoolSplit(int Grey, int Academic, IReadOnlySet<int> AcademicReferences)
    {
        public int GreyOf(IEnumerable<int> references) => references.Distinct().Count(r => !AcademicReferences.Contains(r));
        public int AcademicOf(IEnumerable<int> references) => references.Distinct().Count(AcademicReferences.Contains);
    }

    /// <summary>The split of a run with a formal pool; null for a grey literature review, whose counts stay in one column.</summary>
    public static PoolSplit? Split(MultivocalReportInput input)
    {
        if (!input.Ledger.HasFormalPool) return null;
        var numbers = MultivocalReporter.SourceNumbers(input.Extraction);
        var extracted = input.Extraction.Sources.Where(x => x.Error == null && numbers.ContainsKey(x.Address)).ToList();
        return new PoolSplit(extracted.Count(x => !x.Formal), extracted.Count(x => x.Formal),
            extracted.Where(x => x.Formal).Select(x => numbers[x.Address]).ToHashSet());
    }

    /// <summary>"12 (15%)": a count with its share of its own pool.</summary>
    public static string CountWithShare(int count, int pool) => $"{count.ToString(CultureInfo.InvariantCulture)} ({Percent(count, pool)})";

    /// <summary>The headers of a value table: one count column, or one per pool and both together.</summary>
    private static string[] CountHeaders(PoolSplit? split) =>
        split == null ? new[] { "Value", "Sources", "Share", "Cited" } : new[] { "Value", "Grey", "Academic", "Both", "Cited" };

    /// <summary>One row of a value table: the value, its counts as <see cref="CountHeaders"/> names them, and its sources.</summary>
    private static IReadOnlyList<string> CountRow(string value, int sources, IReadOnlyList<int> references, int extracted, PoolSplit? split, string cited) =>
        split == null
            ? new[] { value, sources.ToString(CultureInfo.InvariantCulture), Percent(sources, extracted), cited }
            : new[] { value, CountWithShare(split.GreyOf(references), split.Grey), CountWithShare(split.AcademicOf(references), split.Academic), CountWithShare(sources, extracted), cited };

    /// <summary>The "not stated" row of a split table: the sources of each pool that state no value of the attribute.</summary>
    private static IReadOnlyList<string> NotStatedRow(int sources, IReadOnlyList<int> stating, int extracted, PoolSplit split) =>
        new[] { "not stated", CountWithShare(split.Grey - split.GreyOf(stating), split.Grey), CountWithShare(split.Academic - split.AcademicOf(stating), split.Academic), CountWithShare(sources, extracted), "" };

    /// <summary>The note under a split table: what each share is of.</summary>
    private static string SplitNote(PoolSplit split) =>
        $"Each share is of its own pool: {split.Grey} grey and {split.Academic} academic sources were extracted, {split.Grey + split.Academic} together.";

    /// <summary>A value's count in the words of the answer: "60 sources, 74% of the grey and 46% of the academic ones" with a split.</summary>
    private static string CountPhrase(int sources, IReadOnlyList<int> references, int extracted, PoolSplit? split) =>
        split == null ? $"{Sources(sources)}, {Percent(sources, extracted)}"
            : $"{split.GreyOf(references)} grey ({Percent(split.GreyOf(references), split.Grey)}) and {split.AcademicOf(references)} academic ({Percent(split.AcademicOf(references), split.Academic)})";
}
