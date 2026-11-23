using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>What the model is given of a formal source's kept text for its extraction, and what that was, for the file.</summary>
/// <param name="Text">The text sent: the whole kept text, or selected passages of it joined by <see cref="FormalExtractionText.Break"/>.</param>
/// <param name="Passages">What was sent, e.g. "14 of 83 passages of the full text (8,104 of 41,230 characters)"; null when the whole kept text was sent.</param>
public sealed record FormalTextForModel(string Text, string? Passages);

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

    /// <summary>What separates two passages that are not next to each other, so no quote can join them.</summary>
    public const string Break = "\n\n[passage break]\n\n";

    // Words for the source's purpose (G12), which the extraction also asks for.
    private const string PurposeWords = "aim purpose objective goal propose present paper article contribution";

    /// <summary>The text to send for a source whose kept text is <paramref name="text"/>, read as its <paramref name="basis"/>.</summary>
    public static FormalTextForModel Select(string text, string? basis, MultivocalPlan plan, MapVersion map)
    {
        if (basis != MultivocalPages.FullTextBasis || text.Length <= WholeUpTo) return new FormalTextForModel(text, null);
        // The kept full text is its passages joined by a blank line (MultivocalPages.AsPaper reads it the same way).
        var passages = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((t, i) => new DocumentChunk("", i + 1, t)).ToList();
        if (passages.Count <= LeadPassages + RelevantPassages) return new FormalTextForModel(text, null);

        var terms = TextRelevance.Terms(string.Join(" ", new[] { plan.Topic, PurposeWords }
            .Concat(plan.Numbered().Select(q => q.Question.Text))
            .Concat(map.Attributes.SelectMany(a => new[] { a.Name, a.Description }.Concat(a.Values)))));
        var lead = passages.Take(LeadPassages).ToList();
        var chosen = lead.Concat(GroundingContextBuilder.SelectChunks(passages.Skip(LeadPassages).ToList(), terms, RelevantPassages))
            .OrderBy(p => p.PageNumber).ToList();

        var parts = new List<string>();
        for (int i = 0; i < chosen.Count; i++)
        {
            if (i > 0) parts.Add(chosen[i].PageNumber == chosen[i - 1].PageNumber + 1 ? "\n\n" : Break);
            parts.Add(chosen[i].Text);
        }
        string sent = string.Concat(parts);
        int sentCharacters = chosen.Sum(p => p.Text.Length);
        return new FormalTextForModel(sent,
            $"{chosen.Count} of {passages.Count} passages of the full text ({sentCharacters.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} of {text.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters)");
    }
}
