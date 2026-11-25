using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// The map's rules, decided in code: the facts code records, the values that only say "not stated", and the checks
// on a map the reviewer saves or the model proposes.
public sealed partial class MultivocalMapper
{
    /// <summary>
    /// Values that only say the source does not state it. Every attribute can be "not stated" for a source, decided in
    /// code during extraction, so these are never values of their own (seen in a live run: seven attributes had one).
    /// </summary>
    public static readonly IReadOnlySet<string> NotStatedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "not specified", "unspecified", "not stated", "not mentioned", "not given", "not reported", "not applicable", "unknown", "n/a", "na", "none specified",
    };

    /// <summary>
    /// The facts the run already knows about every source, recorded by code for each one at extraction; the map never
    /// asks the model for them.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Description)> KnownFacts = new[]
    {
        ("Kind of source", "Blog post, Q&A, code repository, report, thesis …, from the search."),
        ("Literature", "Grey or formal (academic), from the pool the source was found in."),
        ("Site", "Where it was published."),
        ("Date", "When it was published, when the search gave a date."),
        ("Quality points", "Its score on the quality checklist, out of 20."),
    };

    /// <summary>The known facts of one source, in the order of <see cref="KnownFacts"/>.</summary>
    public static IReadOnlyList<(string Name, string Value)> KnownFactsFor(GreyRecord record, GreyQuality? quality) =>
        record.Kind == MultivocalSearcher.FormalKind
            ? new[]
            {
                ("Kind of source", "Academic literature"),
                ("Literature", "Formal (academic)"),
                ("Site", record.Site),
                ("Date", record.Published is DateTime fd ? fd.ToString("yyyy-MM-dd") : "not given"),
                // A formal source is appraised with MMAT, not scored on Table 7 (block I-4); its text basis is recorded too (decision 8).
                ("Quality points", quality?.Mmat is { } mmat ? $"not scored; MMAT {MmatSummary(mmat)}" : "not assessed"),
                ("Text", quality?.TextBasis ?? "not kept"),
            }
            : new[]
            {
                ("Kind of source", MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == record.Kind).Label ?? record.Kind),
                ("Literature", "Grey"),
                ("Site", record.Site),
                ("Date", record.Published is DateTime d ? d.ToString("yyyy-MM-dd") : "not given"),
                ("Quality points", quality is { Outcome: not "Not assessed" } q ? $"{q.Points:0.#} of {GreyQualityChecklist.MaxPoints}" : "not assessed"),
            };

    /// <summary>An MMAT appraisal in a few words: "qualitative, 2 of 5 criteria met", or that the study is not empirical.</summary>
    public static string MmatSummary(StudyExtraction mmat) =>
        mmat.Appraisal.Count == 0 ? (mmat.AppraisalCategory == StudyExtractor.NotAppraised ? "not appraised" : "not empirical, so not appraised")
        : $"{mmat.AppraisalCategory.Replace('_', ' ')}, {mmat.AppraisalYes} of {mmat.Appraisal.Count} criteria met";

    /// <summary>"ToolOrFramework" becomes "Tool or framework"; a name with spaces is kept as written.</summary>
    public static string PlainName(string name)
    {
        string n = name.Trim();
        if (n.Contains(' ') || !System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Z][a-z]+([A-Z][a-z]+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))) return n;
        var words = System.Text.RegularExpressions.Regex.Matches(n, "[A-Z][a-z]+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Value).ToList();
        return string.Join(" ", words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }

    /// <summary>
    /// Trims every text, makes names plain words, drops empty and repeated values and values that only say "not stated",
    /// and keeps no values on an open attribute.
    /// </summary>
    public static List<MapAttribute> Clean(IEnumerable<MapAttribute> attributes) => attributes.Select(a => new MapAttribute
    {
        Name = PlainName(ReviewInputGuard.Normalize(a.Name ?? "", singleLine: true)),
        Question = (a.Question ?? "").Trim().ToUpperInvariant(),
        Description = ReviewInputGuard.Normalize(a.Description ?? "", singleLine: true),
        Open = a.Open,
        Multiple = a.Multiple,
        AlsoFor = (a.AlsoFor ?? new()).Select(q => (q ?? "").Trim().ToUpperInvariant())
            .Where(q => q.Length > 0 && q != (a.Question ?? "").Trim().ToUpperInvariant()).Distinct().ToList(),
        Values = a.Open ? new() : (a.Values ?? new())
            .Select(v => ReviewInputGuard.Normalize(v ?? "", singleLine: true))
            .Where(v => v.Length > 0 && !NotStatedValues.Contains(v.TrimEnd('.')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
    }).ToList();

    /// <summary>What is wrong with a map, or null when it can be used: every attribute named once, for a question of the plan, with values unless open.</summary>
    public static string? Problem(IReadOnlyList<MapAttribute> attributes, MultivocalPlan plan)
    {
        if (attributes.Count == 0) return "The map needs at least one attribute.";
        if (attributes.Count > MaxAttributes) return $"Use at most {MaxAttributes} attributes.";
        var questions = plan.Numbered().Select(q => q.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attributes)
        {
            if (a.Name.Length == 0) return "Give every attribute a name.";
            if (!questions.Contains(a.Question)) return $"\"{a.Name}\" must belong to one of the research questions ({string.Join(", ", questions)}).";
            if (a.AlsoFor.FirstOrDefault(q => !questions.Contains(q)) is { } unknown) return $"\"{a.Name}\" names {unknown} in alsoFor, which is not one of the research questions ({string.Join(", ", questions)}).";
            if (new[] { a.Name, a.Description }.Concat(a.Values).Any(t => t.Length > MaxText)) return $"Keep every text in \"{a.Name}\" under {MaxText} characters.";
            if (!a.Open && a.Values.Count < 2) return $"\"{a.Name}\" needs at least two values, or mark it as open text.";
            if (a.Values.Count > MaxValues) return $"\"{a.Name}\" can have at most {MaxValues} values.";
        }
        var repeated = attributes.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return repeated != null ? $"Two attributes are called \"{repeated.Key}\"." : null;
    }

    /// <summary>
    /// Whether an attribute only records a fact the run already knows for every source (its kind, grey or formal, site,
    /// date or quality). Such an attribute is dropped from the model's map in code rather than sent back: a question
    /// such as "how often in grey and in academic sources?" makes the model add one however it is told (seen in a
    /// live run, twice in a row), and the known fact answers it anyway.
    /// </summary>
    public static bool IsKnownFact(MapAttribute attribute) => System.Text.RegularExpressions.Regex.IsMatch(attribute.Name,
        @"^(source ?type|type of source|kind of source|grey or academic|grey or formal|literature( type)?|publication (date|site|year)|date|site|quality( points| score)?)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// What is wrong with the model's map, beyond <see cref="Problem"/>, once the attributes for known facts are
    /// dropped: more than three attributes for one question, or a value that is also another attribute's value.
    /// </summary>
    public static string? ModelProblem(IReadOnlyList<MapAttribute> proposed, MultivocalPlan plan)
    {
        var attributes = proposed.Where(a => !IsKnownFact(a)).ToList();
        if (Problem(attributes, plan) is { } problem) return problem;
        // A relationship is answered by pairing its two sides per source (issue #458); one side alone can only collect outcomes.
        var oneSided = MultivocalRelationship.Questions(plan).FirstOrDefault(q => attributes.Count(a => a.Serves(q.Number)) == 1);
        if (oneSided.Number != null)
            return $"{oneSided.Number} asks whether two things are related, but only \"{attributes.First(a => a.Serves(oneSided.Number)).Name}\" serves it; give it one attribute for each side, or add {oneSided.Number} to the \"alsoFor\" list of an attribute that records the other side.";
        var crowded = attributes.GroupBy(a => a.Question).FirstOrDefault(g => g.Count() > MaxModelAttributesPerQuestion);
        if (crowded != null) return $"{crowded.Key} has {crowded.Count()} attributes; give each question at most {MaxModelAttributesPerQuestion}.";
        var shared = attributes
            .SelectMany(a => a.Values.Where(v => !v.Equals("Other", StringComparison.OrdinalIgnoreCase)).Select(v => (Value: v, a.Name)))
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Select(x => x.Name).Distinct().Count() > 1);
        return shared != null ? $"\"{shared.Key}\" is a value of both {string.Join(" and ", shared.Select(x => $"\"{x.Name}\"").Distinct())}; keep each value in one attribute." : null;
    }
}
