using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>What the link check decided for one link of a diagram, with the verdicts it rests on (artifact-links.json).</summary>
public sealed class ArtifactLinkCheck
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>The link as the sentence that was checked: "&lt;from&gt; &lt;label&gt; &lt;to&gt; [shared studies]".</summary>
    public string Sentence { get; set; } = "";
    /// <summary>The studies cited by both boxes: the only ones that can state the link.</summary>
    public List<int> SharedReferences { get; set; } = new();
    /// <summary><see cref="ArtifactLinks.Supported"/> or <see cref="ArtifactLinks.Reading"/>.</summary>
    public string Check { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<CitationSupportResult> Verdicts { get; set; } = new();
}

/// <summary>
/// The check of a diagram's links (issue #571). A link claims how two findings relate, so it is checked as the
/// sentence "&lt;from&gt; &lt;label&gt; &lt;to&gt;" against the studies both boxes cite. It is kept as a finding only when one of them
/// supports it with a verbatim quote; otherwise it is drawn dashed, as the review's own reading. The link is never
/// rewritten: the check only decides how it is drawn, and every decision is in artifact-links.json.
/// </summary>
public static class ArtifactLinks
{
    public const string Supported = "supported";
    public const string Reading = "reading";
    public const string FileName = "artifact-links.json";

    private static readonly Regex Marker = new(@"\s*\[\d+(?:\s*[,–-]\s*\d+)*\]", RegexOptions.Compiled);

    /// <summary>The label of a box without its citation markers or closing full stop.</summary>
    public static string Statement(string label) => Marker.Replace(label ?? "", "").Trim().TrimEnd('.', ';', ',').Trim();

    /// <summary>The studies cited by both boxes of a link.</summary>
    public static List<int> SharedReferences(ArtifactNode from, ArtifactNode to) =>
        CitationSupportChecker.ReferencesIn(from.Label).Intersect(CitationSupportChecker.ReferencesIn(to.Label)).OrderBy(r => r).ToList();

    /// <summary>The link as one sentence, citing the shared studies (at most three, the citation rule of the report).</summary>
    public static string Sentence(ReviewArtifact artifact, ArtifactEdge edge, IReadOnlyList<int> shared)
    {
        var from = artifact.Nodes.First(n => n.Id == edge.From);
        var to = artifact.Nodes.First(n => n.Id == edge.To);
        string relation = string.IsNullOrWhiteSpace(edge.Label)
            ? (artifact.Kind == ArtifactKinds.Flowchart ? "leads to" : "is related to")
            : edge.Label.Trim();
        string cite = shared.Count == 0 ? "" : $" [{string.Join(", ", shared.Take(ReviewWriter.MaxReferencesPerSentence))}]";
        return $"{Statement(from.Label)} {relation} {Statement(to.Label)}{cite}.";
    }

    /// <summary>
    /// Checks every link of a diagram with the citation check (no repair), sets <see cref="ArtifactEdge.Check"/> and
    /// returns the decisions. Tables, lists and diagrams without links are left alone.
    /// </summary>
    public static async Task<List<ArtifactLinkCheck>> CheckAsync(IChatCompletionService chat, ReviewArtifact? artifact,
        IReadOnlyList<ReferencedPaper> papers, IReadOnlyDictionary<int, IReadOnlyList<string>> verifiedQuotes,
        int parallelism = 1, string review = CitationRepairer.DefaultReview, CheckCache? cache = null)
    {
        var decisions = new List<ArtifactLinkCheck>();
        if (artifact is not { Error: null, IsDiagram: true } || artifact.Edges.Count == 0) return decisions;
        var ids = artifact.Nodes.Select(n => n.Id).ToHashSet();
        var toCheck = new List<CitedSentence>();
        var decisionOf = new Dictionary<string, (ArtifactEdge Edge, ArtifactLinkCheck Decision)>();
        for (int i = 0; i < artifact.Edges.Count; i++)
        {
            var edge = artifact.Edges[i];
            if (!ids.Contains(edge.From) || !ids.Contains(edge.To)) continue;
            var shared = SharedReferences(artifact.Nodes.First(n => n.Id == edge.From), artifact.Nodes.First(n => n.Id == edge.To))
                .Take(ReviewWriter.MaxReferencesPerSentence).ToList();
            var decision = new ArtifactLinkCheck { From = edge.From, To = edge.To, Label = edge.Label, SharedReferences = shared, Sentence = Sentence(artifact, edge, shared) };
            decisions.Add(decision);
            if (shared.Count == 0)
            {
                decision.Check = Reading;
                decision.Reason = "No study is cited by both boxes, so no single study can state this link.";
                edge.Check = Reading;
            }
            else
            {
                toCheck.Add(new CitedSentence(Field(i), 0, decision.Sentence, shared));
                decisionOf[Field(i)] = (edge, decision);
            }
        }

        var results = toCheck.Count == 0 ? new List<CitationSupportResult>()
            : await CitationSupportChecker.CheckAsync(chat, toCheck, papers, verifiedFindings: verifiedQuotes,
                parallelism: parallelism, secondCheck: true, review: review, cache: cache);
        foreach (var sentence in toCheck)
        {
            var (edge, decision) = decisionOf[sentence.Field];
            decision.Verdicts = results.Where(r => r.Field == sentence.Field).ToList();
            var support = decision.Verdicts.FirstOrDefault(r => r.Verdict == CitationSupportChecker.Supported && r.QuoteVerified);
            decision.Check = support != null ? Supported : Reading;
            decision.Reason = support != null
                ? $"Study [{support.Reference}], cited by both boxes, states the link."
                : "None of the studies cited by both boxes states the link with a quote found in its text.";
            edge.Check = decision.Check;
        }
        return decisions;
    }

    /// <summary>The field name a link's verdicts are recorded under.</summary>
    public static string Field(int edgeIndex) => $"artifact-e{edgeIndex + 1}";

    /// <summary>
    /// The sentence of the synthesis methods on what was checked in a diagram, written from the link decisions;
    /// empty for tables, lists and runs from before the link check.
    /// </summary>
    public static string MethodsSentence(ReviewArtifact? artifact)
    {
        if (artifact is not { Error: null, IsDiagram: true } || artifact.Edges.Count == 0 || artifact.Edges.Any(e => e.Check.Length == 0)) return "";
        int supported = artifact.Edges.Count(e => e.Check == Supported), reading = artifact.Edges.Count - supported;
        string links = artifact.Edges.Count == 1 ? "Its one link" : $"Each of its {artifact.Edges.Count} links";
        string outcome = reading == 0
            ? (artifact.Edges.Count == 1 ? "it is supported." : "all are supported.")
            : supported == 0
                ? (reading == 1 ? "it is not supported, so it is drawn dashed as the review's own reading." : "none is supported, so all are drawn dashed as the review's own reading.")
                : $"{supported} {(supported == 1 ? "is" : "are")} supported, and the other {reading} {(reading == 1 ? "is" : "are")} drawn dashed as the review's own reading.";
        return $" {links} was checked as a sentence against the studies that both of its boxes cite, without repair; {outcome} The verdicts are in {FileName}.";
    }

    /// <summary>The note under a diagram with dashed links; empty when every link is supported or none was checked.</summary>
    public static string DashedNote(ReviewArtifact? artifact) =>
        artifact?.Edges.Any(e => e.Check == Reading) == true
            ? "Dashed links are the review's own reading: no single included study states them."
            : "";

    /// <summary>Writes the decisions to artifact-links.json in the run folder (nothing when there are none).</summary>
    public static async Task WriteAsync(string folder, List<ArtifactLinkCheck> decisions)
    {
        if (decisions.Count == 0) return;
        await File.WriteAllTextAsync(Path.Join(folder, FileName), JsonSerializer.Serialize(decisions, new JsonSerializerOptions { WriteIndented = true }));
    }
}
