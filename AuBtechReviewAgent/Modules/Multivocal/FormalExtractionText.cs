using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// The text a formal (academic) source is extracted from (block I-5). A long open full text is not sent whole: the
/// model gets its first passages, where a paper's title and abstract are, and the passages most relevant to the map's
/// attributes and the review's questions, chosen as the systematic review chooses the passages of its MMAT extraction
/// (<see cref="GroundingContextBuilder.SelectChunks"/>). Every passage is copied from the kept text, so a quote found in
/// what was sent is also found in the kept text the later checks read. An abstract, or a short full text, is sent whole.
/// </summary>
public static class FormalExtractionText
{
    /// <summary>Up to this many characters, the kept text is sent whole.</summary>
    public const int WholeUpTo = 8_000;

    /// <summary>The first passages, always sent: a paper's title, authors and abstract.</summary>
    public const int LeadPassages = 2;

    /// <summary>The passages chosen by relevance on top of the lead passages.</summary>
    public const int RelevantPassages = 12;

    // Words for the source's purpose (G12), which the extraction also asks for.
    private const string PurposeWords = "aim purpose objective goal propose present paper article contribution";

    /// <summary>The text to send for a source whose kept text is <paramref name="text"/>, read as its <paramref name="basis"/>.</summary>
    public static TextForModel Select(string text, string? basis, MultivocalPlan plan, MapVersion map)
    {
        if (basis != MultivocalPages.FullTextBasis || text.Length <= WholeUpTo) return new TextForModel(text, null);
        // The kept full text is its passages joined by a blank line (MultivocalPages.AsPaper reads it the same way).
        var passages = PassageSelection.Paragraphs(text);
        if (passages.Count <= LeadPassages + RelevantPassages) return new TextForModel(text, null);
        var chosen = passages.Take(LeadPassages)
            .Concat(PassageSelection.MostRelevant(text, passages.Skip(LeadPassages).ToList(), Terms(plan, map), RelevantPassages));
        return PassageSelection.Join(text, chosen, passages.Count, "the full text");
    }

    /// <summary>The words an extraction looks for: the topic, a source's purpose, the questions and the map's attributes.</summary>
    public static HashSet<string> Terms(MultivocalPlan plan, MapVersion map) => TextRelevance.Terms(string.Join(" ", new[] { plan.Topic, PurposeWords }
        .Concat(plan.Numbered().Select(q => q.Question.Text))
        .Concat(map.Attributes.SelectMany(a => new[] { a.Name, a.Description }.Concat(a.Values)))));
}
