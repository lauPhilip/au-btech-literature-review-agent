using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// A relationship question ("are X and Y related?", Garousi et al. 2019) answered in code (issue #458). In a real run
/// such a question was answered by grouping every finding by its outcome, so a theme called "Retrieval reduces
/// failures" held findings that had nothing to do with retrieval, and the abstract concluded that it did. Counting
/// outcomes can never show a relationship. Now the map serves such a question with an attribute for each side, and the
/// answer pairs, per source, the values of the two: a table of how many sources state both. When no source states
/// both, or the map has only one side, the answer says in code that the sources do not allow the question to be
/// answered, the facts for the front matter say so, and an abstract that claims the relationship is sent back.
/// </summary>
public static class MultivocalRelationship
{
    public const string Type = "relationship";

    /// <summary>The pairing of one relationship question: its two sides, the sources that state both, and the pairs.</summary>
    public sealed record Pairing(MapAttribute? X, MapAttribute? Y, int Extracted, IReadOnlyList<int> Both,
        IReadOnlyList<(string X, string Y, IReadOnlyList<int> References)> Pairs)
    {
        public bool Answerable => X != null && Y != null && Both.Count > 0;
    }

    /// <summary>The relationship questions of the plan, by number.</summary>
    public static IEnumerable<(string Number, PlannedQuestion Question)> Questions(MultivocalPlan plan) =>
        plan.Numbered().Where(q => q.Question.Type == Type);

    /// <summary>The values of the first two attributes that serve the question, paired per source; a side is null when the map has no attribute for it.</summary>
    public static Pairing Pair(MultivocalReportInput input, string number)
    {
        var map = input.Map.Versions.FirstOrDefault(v => v.Number == input.Map.FixedVersion) ?? input.Map.Latest;
        var sides = map.Attributes.Where(a => a.Serves(number)).Take(2).ToList();
        var numbers = MultivocalReporter.SourceNumbers(input.Extraction);
        int extracted = input.Extraction.Sources.Count(x => x.Error == null);
        if (sides.Count < 2) return new Pairing(sides.FirstOrDefault(), null, extracted, Array.Empty<int>(), Array.Empty<(string, string, IReadOnlyList<int>)>());
        Dictionary<string, List<string>> ValuesOf(MapAttribute a) => input.Synthesis.Findings
            .Where(f => f.Attribute == a.Name && numbers.ContainsKey(f.Address) && !MultivocalSynthesiser.IsNotStated(f.Value))
            .GroupBy(f => f.Address).ToDictionary(g => g.Key, g => g.Select(f => f.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var xs = ValuesOf(sides[0]);
        var ys = ValuesOf(sides[1]);
        var both = xs.Keys.Where(ys.ContainsKey).ToList();
        var pairs = both.SelectMany(address => xs[address].SelectMany(x => ys[address].Select(y => (X: x, Y: y, Reference: numbers[address]))))
            .GroupBy(p => (X: p.X.ToLowerInvariant(), Y: p.Y.ToLowerInvariant()))
            .Select(PairOf)
            .OrderByDescending(p => p.References.Count).ThenBy(p => p.X, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Y, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Pairing(sides[0], sides[1], extracted, both.Select(a => numbers[a]).OrderBy(n => n).ToList(), pairs);
    }

    private static (string X, string Y, IReadOnlyList<int> References) PairOf(IGrouping<(string X, string Y), (string X, string Y, int Reference)> g) =>
        (g.First().X, g.First().Y, g.Select(p => p.Reference).Distinct().OrderBy(n => n).ToList());

    /// <summary>Why the sources do not allow the question to be answered, in code; null when they do.</summary>
    public static string? NotAnswerableText(Pairing p) =>
        p.X == null ? "The systematic map has no attribute for this question, so the sources do not allow the relationship to be answered."
        : p.Y == null ? $"The systematic map records only \"{p.X.Name}\" for this question, not both sides of the relationship, so no source can be counted for both; the sources do not allow the relationship to be answered."
        : p.Both.Count == 0 ? $"No extracted source states both a value of \"{p.X.Name}\" and a value of \"{p.Y.Name}\", so the sources do not report both and do not allow the relationship to be answered."
        : null;

    /// <summary>The sentence before the table of a relationship question, or its whole answer when it cannot be answered.</summary>
    public static string Lead(MultivocalReportInput input, string number)
    {
        var p = Pair(input, number);
        return NotAnswerableText(p) ?? $"This question asks whether two things are related, so it is answered by pairing, in code: for each source that states both a value of \"{p.X!.Name}\" and a value of \"{p.Y!.Name}\", the table counts the pair. {Sources(p.Both.Count)} of the {p.Extracted} extracted sources state both. A count says how often two values are reported together by the same source, not that one causes the other.";
    }

    /// <summary>The table of pairs, with the sources that state each; none when the question cannot be answered.</summary>
    public static IReadOnlyList<PaperTable> Tables(MultivocalReportInput input, string number)
    {
        var p = Pair(input, number);
        if (!p.Answerable) return Array.Empty<PaperTable>();
        var rows = p.Pairs.Select(x => (IReadOnlyList<string>)new[] { x.X, x.Y, x.References.Count.ToString(CultureInfo.InvariantCulture), $"[{string.Join(", ", x.References)}]" }).ToList();
        return new[] { new PaperTable($"{p.X!.Name} and {p.Y!.Name}", new[] { p.X.Name, p.Y.Name, "Sources", "Cited" }, rows,
            "A source that states several values of a side counts once for each pair it states.") };
    }

    /// <summary>The answer in a few sentences, in code, for the answers to the research questions.</summary>
    public static string Summary(MultivocalReportInput input, string number)
    {
        var p = Pair(input, number);
        if (NotAnswerableText(p) is { } no) return no;
        var top = p.Pairs.Take(MultivocalPaperParts.TopValues).Select(x => $"\"{x.X}\" with \"{x.Y}\" ({Sources(x.References.Count)})").ToList();
        string list = top.Count == 1 ? top[0] : string.Join(", ", top.Take(top.Count - 1)) + " and " + top[^1];
        return $"{Sources(p.Both.Count)} of the {p.Extracted} extracted sources state both \"{p.X!.Name}\" and \"{p.Y!.Name}\"; the pairs stated most often are {list}. Counted in code; a pair reported together does not show that one causes the other.";
    }

    /// <summary>The line on the relationship questions for the facts of the front matter; empty without any.</summary>
    public static string FactsLine(MultivocalReportInput input)
    {
        var lines = Questions(input.Planned.Plan).Select(q =>
        {
            var p = Pair(input, q.Number);
            return p.Answerable
                ? $"{q.Number} asks about a relationship; it is answered by counting the {p.Both.Count} sources that state both sides, which shows what is reported together, not a cause."
                : $"{q.Number} asks about a relationship that the sources do not allow to be answered: {Lower(NotAnswerableText(p)!)} Do not say in the abstract whether they are related.";
        });
        return string.Join(" ", lines.Select(l => l + " "));
    }

    private static readonly Regex RelationWord = new(
        @"\b(?:reduc\w*|increas\w*|improv\w*|lower\w*|rais\w*|fewer|related|relationship|associat\w*|linked|correlat\w*|leads?\s+to|caus\w*|prevent\w*|decreas\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex SaysNotAnswered = new(
        @"\b(?:cannot|can't|could\s+not|do(?:es)?\s+not|did\s+not|not\s+(?:be\s+)?answer\w*|no\s+source|none\s+of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
        { "which", "there", "their", "about", "between", "related", "reported", "sources", "source", "practice", "practices", "these", "those", "where", "using" };

    /// <summary>
    /// Why an abstract claims a relationship the sources do not allow to be answered, or null: a sentence that uses a word
    /// of relation (reduces, related, associated …) with two of the question's own words, and does not say it cannot be answered.
    /// </summary>
    public static string? AbstractProblem(string abstractText, MultivocalReportInput input)
    {
        if (string.IsNullOrWhiteSpace(abstractText)) return null;
        foreach (var (number, question) in Questions(input.Planned.Plan))
        {
            var p = Pair(input, number);
            if (p.Answerable) continue;
            // The topic's own words ("context", "agents") are in most sentences of the abstract, so they do not count.
            string topic = input.Planned.Plan.Topic.ToLowerInvariant();
            var words = Regex.Matches(question.Text, @"\p{L}{5,}", RegexOptions.None, TimeSpan.FromSeconds(1))
                .Select(m => m.Value.ToLowerInvariant()).Where(w => !CommonWords.Contains(w) && !topic.Contains(Stem(w), StringComparison.Ordinal)).Distinct().ToList();
            foreach (string sentence in CitationSupportChecker.SplitSentences(abstractText))
            {
                string lower = sentence.ToLowerInvariant();
                int shared = words.Count(w => Regex.IsMatch(lower, $@"\b{Regex.Escape(Stem(w))}", RegexOptions.None, TimeSpan.FromSeconds(1)));
                if (shared >= 2 && RelationWord.IsMatch(sentence) && !SaysNotAnswered.IsMatch(sentence))
                    return $"the abstract says \"{sentence.Trim()}\", but {number} asks about a relationship the sources do not allow to be answered ({Lower(NotAnswerableText(p)!)}); say that it could not be answered, or leave it out.";
            }
        }
        return null;
    }

    // "failures" and "failure", "retrieval" and "retrieve": the first five letters are enough to find the word again.
    private static string Stem(string word) => word.Length <= 6 ? word : word[..Math.Max(5, word.Length - 3)];

    private static string Sources(int n) => n == 1 ? "1 source" : $"{n} sources";

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
