using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

public class ArtifactNode
{
    public string Id { get; set; } = "";
    /// <summary>What the box says, with [n] citations; checked like a sentence of the report.</summary>
    public string Label { get; set; } = "";
}

public class ArtifactEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>
/// The output the reviewer asked for (a flowchart, a concept map, a comparison table or a bullet list),
/// built from the review's own findings after the synthesis. Every node label, table cell and bullet that
/// cites a study is checked like a sentence of the report. Diagrams are described as nodes and edges and
/// drawn in code (Mermaid for the page, TikZ for main.tex), so a model never writes diagram syntax.
/// </summary>
public class ReviewArtifact
{
    /// <summary>What was built: flowchart, concept-map, table or bullets.</summary>
    public string Kind { get; set; } = "";
    /// <summary>What the reviewer chose (one of <see cref="ArtifactKinds"/>).</summary>
    public string Requested { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string Title { get; set; } = "";
    public string Caption { get; set; } = "";
    public List<ArtifactNode> Nodes { get; set; } = new();
    public List<ArtifactEdge> Edges { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public List<string> Bullets { get; set; } = new();
    /// <summary>Why no artifact could be built (the report says so instead of showing something invented).</summary>
    public string? Error { get; set; }
    /// <summary>Mermaid source drawn from Nodes and Edges (empty for tables and lists).</summary>
    public string Mermaid { get; set; } = "";

    [JsonIgnore] public bool IsDiagram => Kind is ArtifactKinds.Flowchart or ArtifactKinds.ConceptMap;

    /// <summary>Every text that can carry citations, with the field name the citation check records it under.</summary>
    public IEnumerable<(string Field, string Text)> CitableTexts()
    {
        for (int i = 0; i < Nodes.Count; i++) yield return ($"artifact-n{i + 1}", Nodes[i].Label);
        for (int r = 0; r < Rows.Count; r++)
            for (int c = 0; c < Rows[r].Count; c++) yield return ($"artifact-r{r + 1}c{c + 1}", Rows[r][c]);
        for (int i = 0; i < Bullets.Count; i++) yield return ($"artifact-b{i + 1}", Bullets[i]);
    }

    /// <summary>
    /// After the repair pass: an element whose sentence was deleted is removed (a node with its edges, a
    /// bullet); an emptied table cell shows a dash, so the table keeps its shape.
    /// </summary>
    public void Compact()
    {
        var gone = Nodes.Where(n => string.IsNullOrWhiteSpace(n.Label)).Select(n => n.Id).ToHashSet();
        Nodes.RemoveAll(n => gone.Contains(n.Id));
        Edges.RemoveAll(e => gone.Contains(e.From) || gone.Contains(e.To));
        Bullets.RemoveAll(string.IsNullOrWhiteSpace);
        foreach (var row in Rows)
            for (int c = 0; c < row.Count; c++)
                if (string.IsNullOrWhiteSpace(row[c])) row[c] = "–";
    }

    /// <summary>Writes a checked (or repaired) text back to the element it came from.</summary>
    public void SetText(string field, string text)
    {
        var n = Regex.Match(field, @"^artifact-n(\d+)$");
        if (n.Success) { int i = int.Parse(n.Groups[1].Value) - 1; if (i < Nodes.Count) Nodes[i].Label = text; return; }
        var rc = Regex.Match(field, @"^artifact-r(\d+)c(\d+)$");
        if (rc.Success)
        {
            int r = int.Parse(rc.Groups[1].Value) - 1, c = int.Parse(rc.Groups[2].Value) - 1;
            if (r < Rows.Count && c < Rows[r].Count) Rows[r][c] = text;
            return;
        }
        var b = Regex.Match(field, @"^artifact-b(\d+)$");
        if (b.Success) { int i = int.Parse(b.Groups[1].Value) - 1; if (i < Bullets.Count) Bullets[i] = text; }
    }
}

/// <summary>The kinds of artifact a reviewer can ask for, with the label and default instruction the form shows.</summary>
public static class ArtifactKinds
{
    public const string None = "none";
    public const string Flowchart = "flowchart";
    public const string ConceptMap = "concept-map";
    public const string Table = "table";
    public const string Bullets = "bullets";
    public const string Custom = "custom";

    public static readonly IReadOnlyList<(string Key, string Label, string Instruction)> All = new[]
    {
        (ConceptMap, "Concept map of the themes", "Show the main concepts the literature discusses and how they relate to each other."),
        (Flowchart, "Flowchart of a process or framework", "Show, step by step, the process or framework the reviewed studies describe."),
        (Table, "Comparison table", "Compare the approaches or studies on the dimensions that matter most for the objective."),
        (Bullets, "Bullet-point list", "List the most important, evidence-backed recommendations for practice."),
        (Custom, "Custom (describe below)", ""),
        (None, "No artifact", ""),
    };

    public static string Normalize(string? kind) => All.Any(k => k.Key == kind) ? kind! : ConceptMap;
    public static string DefaultInstruction(string kind) => All.FirstOrDefault(k => k.Key == kind).Instruction ?? "";
}

/// <summary>Builds the artifact with one model call, validates it in code, and draws diagrams.</summary>
public static class ArtifactBuilder
{
    public const int MaxNodes = 16, MaxEdges = 24, MaxColumns = 6, MaxRows = 15, MaxBullets = 12;

    private sealed class Answer
    {
        public string? Format { get; set; }
        public string? Title { get; set; }
        public string? Caption { get; set; }
        public List<ArtifactNode>? Nodes { get; set; }
        public List<ArtifactEdge>? Edges { get; set; }
        public List<string>? Columns { get; set; }
        public List<List<string>>? Rows { get; set; }
        public List<string>? Bullets { get; set; }
    }

    private static string Shape(string kind) => kind switch
    {
        ArtifactKinds.Flowchart or ArtifactKinds.ConceptMap =>
            $$"""{"title":"...","caption":"one sentence on what the diagram shows","nodes":[{"id":"A","label":"short statement with [n]"}],"edges":[{"from":"A","to":"B","label":"optional short relation"}]}  (at most {{MaxNodes}} nodes and {{MaxEdges}} edges; node labels at most 15 words)""",
        ArtifactKinds.Table =>
            $$"""{"title":"...","caption":"one sentence on what the table compares","columns":["...","..."],"rows":[["cell with [n]","..."]]}  (2 to {{MaxColumns}} columns, at most {{MaxRows}} rows, every row as long as the columns)""",
        _ =>
            $$"""{"title":"...","caption":"one sentence on what the list contains","bullets":["one point with [n]"]}  (3 to {{MaxBullets}} bullets, each one or two sentences)""",
    };

    /// <summary>Asks for the artifact, validates it and returns it (with Error set when it could not be built).</summary>
    public static async Task<ReviewArtifact> BuildAsync(IChatCompletionService chat, string requested, string instruction,
        string objective, string results, string referenceList, int referenceCount)
    {
        var artifact = new ReviewArtifact { Requested = requested, Instruction = instruction };
        string formats = requested == ArtifactKinds.Custom
            ? $"Choose the format that best fits the reviewer's request and set \"format\" to one of: flowchart, concept-map, table, bullets. Then use the matching structure:\n- flowchart or concept-map: {Shape(ArtifactKinds.Flowchart)}\n- table: {Shape(ArtifactKinds.Table)}\n- bullets: {Shape(ArtifactKinds.Bullets)}"
            : $"Use this structure (\"format\" is \"{requested}\"):\n{Shape(requested)}";
        string what = requested switch
        {
            ArtifactKinds.Flowchart => "a flowchart: boxes are steps or stages, arrows show their order",
            ArtifactKinds.ConceptMap => "a concept map: boxes are concepts or findings, labelled arrows say how they relate",
            ArtifactKinds.Table => "a comparison table",
            ArtifactKinds.Bullets => "a bullet-point list",
            _ => "the artifact the reviewer describes",
        };

        string prompt = $$"""
            You are turning the results of a systematic literature review into {{what}}. The artifact summarises what the reviewed studies found; it is not new research.

            REVIEW OBJECTIVE: "{{objective}}"
            THE REVIEWER'S REQUEST FOR THE ARTIFACT: "{{(string.IsNullOrWhiteSpace(instruction) ? ArtifactKinds.DefaultInstruction(requested) : instruction)}}"
            {{PromptSafety.ReviewerInputNotice}}

            REFERENCE LIST (cite ONLY these numbers):
            {{referenceList}}

            RESULTS OF THE REVIEW (the only source you may use; every citation below has been checked against its paper):
            {{results}}

            RULES:
            1. Build every element from the results above. Do not add facts, numbers or studies that are not in them.
            2. An element that states a finding ends with the citation the results give for it, for example "... [3]" or "... [2, 5]". Elements that only name a concept need no citation.
            3. Keep it focused on the reviewer's request; if the results cannot support the request, build the closest artifact they do support and say so in the caption.
            4. Plain text only inside the fields: no Markdown.

            {{formats}}
            Respond ONLY with a valid minified JSON object that includes "format".
            """;

        try
        {
            var answer = await LlmJson.GetAsync<Answer>(chat, prompt, PrismaReviewEngine.JsonMode(0.2), a => Validate(a, requested, referenceCount));
            string kind = requested == ArtifactKinds.Custom ? Normalize(a: answer.Format) : requested;
            artifact.Kind = kind;
            artifact.Title = Clean(answer.Title, 120);
            artifact.Caption = Clean(answer.Caption, 400);
            if (kind is ArtifactKinds.Flowchart or ArtifactKinds.ConceptMap)
            {
                var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in answer.Nodes!.Take(MaxNodes))
                {
                    if (ids.ContainsKey(node.Id.Trim())) continue;
                    ids[node.Id.Trim()] = $"n{ids.Count + 1}";
                    artifact.Nodes.Add(new ArtifactNode { Id = ids[node.Id.Trim()], Label = Clean(node.Label, 200) });
                }
                artifact.Edges = (answer.Edges ?? new()).Take(MaxEdges)
                    .Where(e => ids.ContainsKey(e.From.Trim()) && ids.ContainsKey(e.To.Trim()))
                    .Select(e => new ArtifactEdge { From = ids[e.From.Trim()], To = ids[e.To.Trim()], Label = Clean(e.Label, 60) })
                    .ToList();
            }
            else if (kind == ArtifactKinds.Table)
            {
                artifact.Columns = answer.Columns!.Select(c => Clean(c, 60)).ToList();
                artifact.Rows = answer.Rows!.Take(MaxRows).Select(r => r.Select(c => Clean(c, 300)).ToList()).ToList();
            }
            else
            {
                artifact.Bullets = answer.Bullets!.Take(MaxBullets).Select(b => Clean(b, 400)).Where(b => b.Length > 0).ToList();
            }
        }
        catch (Exception ex)
        {
            artifact.Error = $"The artifact could not be built from the results ({ex.GetType().Name}).";
        }
        return artifact;
    }

    private static string Normalize(string? a) => a?.Trim().ToLowerInvariant() switch
    {
        "flowchart" => ArtifactKinds.Flowchart,
        "concept-map" or "concept map" or "conceptmap" => ArtifactKinds.ConceptMap,
        "table" => ArtifactKinds.Table,
        _ => ArtifactKinds.Bullets,
    };

    private static string? Validate(Answer a, string requested, int referenceCount)
    {
        string kind = requested == ArtifactKinds.Custom ? Normalize(a.Format) : requested;
        if (requested == ArtifactKinds.Custom && a.Format?.Trim().ToLowerInvariant() is not ("flowchart" or "concept-map" or "concept map" or "table" or "bullets"))
            return "format must be one of flowchart, concept-map, table, bullets.";
        IEnumerable<string> texts;
        if (kind is ArtifactKinds.Flowchart or ArtifactKinds.ConceptMap)
        {
            if (a.Nodes == null || a.Nodes.Count < 2) return "nodes must list at least two boxes.";
            if (a.Nodes.Count > MaxNodes) return $"use at most {MaxNodes} nodes.";
            if (a.Nodes.Any(n => string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.Label))) return "every node needs an id and a label.";
            var ids = a.Nodes.Select(n => n.Id.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if ((a.Edges ?? new()).Any(e => !ids.Contains(e.From?.Trim() ?? "") || !ids.Contains(e.To?.Trim() ?? ""))) return "every edge must connect two node ids from the nodes list.";
            texts = a.Nodes.Select(n => n.Label);
        }
        else if (kind == ArtifactKinds.Table)
        {
            if (a.Columns == null || a.Columns.Count < 2 || a.Columns.Count > MaxColumns) return $"columns must have 2 to {MaxColumns} entries.";
            if (a.Rows == null || a.Rows.Count == 0) return "rows must not be empty.";
            if (a.Rows.Any(r => r == null || r.Count != a.Columns.Count)) return $"every row must have exactly {a.Columns.Count} cells.";
            texts = a.Rows.SelectMany(r => r);
        }
        else
        {
            if (a.Bullets == null || a.Bullets.Count(b => !string.IsNullOrWhiteSpace(b)) < 2) return "bullets must have at least two entries.";
            texts = a.Bullets;
        }
        var outside = texts.SelectMany(t => CitationSupportChecker.ReferencesIn(t ?? "")).Where(r => r < 1 || r > referenceCount).Distinct().ToList();
        return outside.Count > 0 ? $"citation numbers {string.Join(", ", outside)} are not in the reference list." : null;
    }

    private static string Clean(string? text, int max)
    {
        string t = ProseCleaner.Clean(text ?? "", "artifact").Text.Replace('\n', ' ').Trim();
        return t.Length > max ? t[..max].TrimEnd() + "…" : t;
    }

    /// <summary>Mermaid source for a diagram artifact. Labels are quoted so brackets and citations are safe.</summary>
    public static string ToMermaid(ReviewArtifact a)
    {
        if (!a.IsDiagram || a.Nodes.Count == 0) return "";
        static string Q(string s) => s.Replace("\"", "'").Replace("#", "no. ").Replace(";", ",");
        var sb = new StringBuilder();
        sb.AppendLine(a.Kind == ArtifactKinds.Flowchart ? "flowchart TD" : "flowchart LR");
        foreach (var n in a.Nodes) sb.AppendLine($"    {n.Id}[\"{Q(n.Label)}\"]");
        foreach (var e in a.Edges)
            sb.AppendLine(string.IsNullOrWhiteSpace(e.Label) ? $"    {e.From} --> {e.To}" : $"    {e.From} -->|\"{Q(e.Label)}\"| {e.To}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A TikZ picture for main.tex: boxes in layers by their longest path from a starting box (left to right
    /// for concept maps, top to bottom for flowcharts), arrows between them. Compiles with plain pdflatex.
    /// </summary>
    public static string ToTikz(ReviewArtifact a, Func<string, string> escape)
    {
        if (!a.IsDiagram || a.Nodes.Count == 0) return "";
        var layer = a.Nodes.ToDictionary(n => n.Id, _ => 0);
        // Longest-path layering; at most Nodes.Count rounds, so a cycle cannot loop forever.
        for (int round = 0; round < a.Nodes.Count; round++)
        {
            bool changed = false;
            foreach (var e in a.Edges)
                if (e.From != e.To && layer[e.To] < layer[e.From] + 1 && layer[e.From] + 1 < a.Nodes.Count) { layer[e.To] = layer[e.From] + 1; changed = true; }
            if (!changed) break;
        }
        bool vertical = a.Kind == ArtifactKinds.Flowchart;
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[box/.style={draw, rounded corners, align=center, text width=3.2cm, font=\scriptsize, inner sep=3pt}, every edge/.style={draw, ->, thick}]");
        foreach (var group in a.Nodes.GroupBy(n => layer[n.Id]).OrderBy(g => g.Key))
        {
            int i = 0;
            foreach (var n in group)
            {
                double x = vertical ? i * 4.0 : group.Key * 4.4;
                double y = vertical ? -group.Key * 2.2 : -i * 2.0;
                sb.AppendLine($"\\node[box] ({n.Id}) at ({x.ToString("0.0", CultureInfo.InvariantCulture)},{y.ToString("0.0", CultureInfo.InvariantCulture)}) {{{escape(n.Label)}}};");
                i++;
            }
        }
        foreach (var e in a.Edges)
        {
            string label = string.IsNullOrWhiteSpace(e.Label) ? "" : $" node[midway, fill=white, font=\\tiny, align=center] {{{escape(e.Label)}}}";
            sb.AppendLine($"\\draw[->, thick] ({e.From}) --{label} ({e.To});");
        }
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }
}
