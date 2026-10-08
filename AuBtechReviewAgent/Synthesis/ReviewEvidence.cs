using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>One finding of a source whose quote was found in the source's own text, in code.</summary>
public sealed record EvidenceFinding(string Id, string Value, string Quote);

/// <summary>
/// One numbered source a review writes from: its reference line, a line on what kind of source it is, its
/// quote-verified findings, and its text (a paper's full text or abstract, or a grey source's kept page text).
/// </summary>
public sealed class EvidenceSource
{
    public int Reference { get; init; }
    public string Title { get; init; } = "";

    /// <summary>The source's line in the reference list, built in code; null when the source is not in the list.</summary>
    public string? ReferenceLine { get; init; }

    /// <summary>What kind of source it is, for the writer (a study's type and method, a grey source's kind and tier); null when unknown.</summary>
    public string? Profile { get; init; }
    public IReadOnlyList<EvidenceFinding> Findings { get; init; } = Array.Empty<EvidenceFinding>();

    /// <summary>The labels of every code of the source, in or out of a theme.</summary>
    public IReadOnlyList<string> Codes { get; init; } = Array.Empty<string>();

    /// <summary>The source's text: its chunks, for excerpts and the citation check, and its abstract.</summary>
    public ReferencedPaper? Text { get; init; }
}

/// <summary>One theme the review reports: its sources, and the codes that put each source in it.</summary>
public sealed class EvidenceTheme
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<int> Sources { get; init; } = Array.Empty<int>();

    /// <summary>The labels of the codes that put each source in this theme.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> Codes { get; init; } = new Dictionary<int, IReadOnlyList<string>>();
}

/// <summary>
/// The evidence a review writes from, in one shape for every kind of review (module design M7): the numbered
/// sources with their verified findings and text, and the themes with their sources and codes. A module fills it
/// from its own extraction and coding; the shared writing steps read only this, so the text of a systematic review
/// and of a multivocal review is grounded in the same way. Nothing in it is written by the model unchecked: findings
/// carry quotes found in the source, themes list sources in code, reference lines are built in code.
/// </summary>
public sealed class ReviewEvidence
{
    /// <summary>What the review is called in prompts, e.g. "systematic literature review".</summary>
    public string ReviewName { get; init; } = CitationRepairer.DefaultReview;

    /// <summary>What a source is called in prompts, e.g. "STUDY".</summary>
    public string SourceLabel { get; init; } = "STUDY";
    public IReadOnlyList<EvidenceSource> Sources { get; init; } = Array.Empty<EvidenceSource>();
    public IReadOnlyList<EvidenceTheme> Themes { get; init; } = Array.Empty<EvidenceTheme>();

    /// <summary>Facts about the evidence base, written in code, for the limitations of the discussion.</summary>
    public string BaseFacts { get; init; } = "";

    private Dictionary<int, EvidenceSource>? _byReference;
    private Dictionary<int, EvidenceSource> ByReference => _byReference ??= Sources.ToDictionary(s => s.Reference);

    public EvidenceSource? Source(int reference) => ByReference.GetValueOrDefault(reference);

    /// <summary>The reference-list lines of the given sources, in number order.</summary>
    public string ReferenceLines(IEnumerable<int> references) =>
        string.Join("\n", references.OrderBy(r => r).Select(r => $"[{r}] - {(ByReference.TryGetValue(r, out var s) && s.ReferenceLine != null ? s.ReferenceLine : "(citation unavailable)")}"));

    /// <summary>The reference-list lines of every source.</summary>
    public string ReferenceLines() => ReferenceLines(Sources.Where(s => s.ReferenceLine != null).Select(s => s.Reference));

    /// <summary>
    /// What the writer gets for one source of a theme: its profile, its codes in the theme, its verified findings
    /// (or its abstract when it has none) and a few excerpts of its text chosen by the theme's words.
    /// </summary>
    public string SourceBlock(int reference, EvidenceTheme? theme, int excerpts)
    {
        var sb = new StringBuilder();
        var source = Source(reference);
        var text = source?.Text;
        sb.AppendLine($"{SourceLabel} [{reference}] {text?.Title}");
        if (!string.IsNullOrEmpty(source?.Profile)) sb.AppendLine(source.Profile);
        var codes = (theme == null ? source?.Codes : theme.Codes.GetValueOrDefault(reference)) ?? Array.Empty<string>();
        if (codes.Count > 0) sb.AppendLine($"Codes{(theme == null ? "" : " in this theme")}: {string.Join("; ", codes)}");
        var findings = source?.Findings ?? Array.Empty<EvidenceFinding>();
        foreach (var f in findings) sb.AppendLine($"Finding {f.Id}: {f.Value} (verbatim: \"{f.Quote}\")");
        if (findings.Count == 0 && text != null && !string.IsNullOrWhiteSpace(text.Abstract))
            sb.AppendLine($"Abstract: {text.Abstract.Trim()}");
        if (text != null && excerpts > 0)
        {
            var terms = TextRelevance.Terms($"{theme?.Name} {theme?.Description} {string.Join(" ", codes)}");
            foreach (var chunk in GroundingContextBuilder.SelectChunks(text.Chunks, terms, excerpts))
                sb.AppendLine($"[{reference}, page {chunk.PageNumber}]: {chunk.Text}");
        }
        return sb.ToString();
    }

    /// <summary>The quotes of each source's verified findings, by reference number, for the citation check.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> VerifiedQuotes() => Sources
        .ToDictionary(s => s.Reference, s => (IReadOnlyList<string>)s.Findings.Select(f => f.Quote).ToList());
}
