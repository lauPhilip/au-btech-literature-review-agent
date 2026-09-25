using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>An included paper together with the text the pipeline has for it.</summary>
public record ReferencedPaper(int ReferenceNumber, string PaperId, string Title, string Abstract, IReadOnlyList<DocumentChunk> Chunks)
{
    public bool HasFullText => Chunks.Count > 0;
}

/// <summary>Simple lexical relevance: shared content words between a text and a query.</summary>
public static class TextRelevance
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","that","this","from","are","was","were","have","has","been","their","they","which",
        "into","such","than","then","also","these","those","there","where","when","what","while","about","between",
        "within","without","using","used","based","over","under","more","most","some","each","other","only","both",
        "can","may","not","but","its","our","via","use","how","why","who","all","any","one","two","new","study","paper",
    };

    public static HashSet<string> Terms(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}\-]{2,}")
            .Select(m => m.Value.Trim('-'))
            .Where(w => w.Length >= 3 && !Stopwords.Contains(w))
            .ToHashSet();

    public static int Score(HashSet<string> queryTerms, string text)
    {
        if (queryTerms.Count == 0) return 0;
        var terms = Terms(text);
        return queryTerms.Count(terms.Contains);
    }
}

/// <summary>
/// Builds the source text the model writes the outline and synthesis from. The old version took the first
/// 40 chunks across all papers, so one long PDF could fill the whole budget and the other papers were
/// represented by nothing at all (their abstract fallback only kicked in when a paper had no chunks). Now
/// every included paper gets its abstract plus an equal share of its most relevant full-text chunks, each
/// labelled with the paper's reference number.
/// </summary>
public static class GroundingContextBuilder
{
    public static string Build(IReadOnlyList<ReferencedPaper> papers, string query, int totalChunkBudget = 40)
    {
        if (papers.Count == 0) return string.Empty;
        var queryTerms = TextRelevance.Terms(query);
        int perPaper = Math.Max(1, totalChunkBudget / papers.Count);

        var sb = new StringBuilder();
        foreach (var paper in papers.OrderBy(p => p.ReferenceNumber))
        {
            sb.AppendLine($"[Reference {paper.ReferenceNumber}] {paper.Title}");
            sb.AppendLine($"Abstract: {(string.IsNullOrWhiteSpace(paper.Abstract) ? "(no abstract in the source metadata)" : paper.Abstract.Trim())}");
            if (!paper.HasFullText) sb.AppendLine("(Full text was not available for this paper; only the abstract above can be used.)");

            foreach (var chunk in SelectChunks(paper.Chunks, queryTerms, perPaper))
            {
                sb.AppendLine($"[Reference {paper.ReferenceNumber}, page {chunk.PageNumber}]: {chunk.Text}");
            }
            sb.AppendLine("---");
        }
        return sb.ToString();
    }

    /// <summary>The <paramref name="count"/> most relevant chunks, returned in page order.</summary>
    public static IReadOnlyList<DocumentChunk> SelectChunks(IReadOnlyList<DocumentChunk> chunks, HashSet<string> queryTerms, int count) =>
        chunks
            .Select((c, i) => (Chunk: c, Index: i, Score: TextRelevance.Score(queryTerms, c.Text)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(count)
            .OrderBy(x => x.Index)
            .Select(x => x.Chunk)
            .ToList();
}
