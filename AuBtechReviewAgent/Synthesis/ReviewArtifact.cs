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
    /// <summary>
    /// What the link check found (<see cref="ArtifactLinks"/>): "supported" when a study cited by both boxes states the
    /// link, "reading" when none does and the link is the review's own reading (drawn dashed); empty on runs from before the check.
    /// </summary>
    public string Check { get; set; } = "";
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
        if (n.Success)
        {
            int i = int.Parse(n.Groups[1].Value) - 1;
            if (i < Nodes.Count) Nodes[i].Label = text;
            return;
        }
        var rc = Regex.Match(field, @"^artifact-r(\d+)c(\d+)$");
        if (rc.Success)
        {
            int r = int.Parse(rc.Groups[1].Value) - 1, c = int.Parse(rc.Groups[2].Value) - 1;
            if (r < Rows.Count && c < Rows[r].Count) Rows[r][c] = text;
            return;
        }
        var b = Regex.Match(field, @"^artifact-b(\d+)$");
        if (b.Success)
        {
            int i = int.Parse(b.Groups[1].Value) - 1;
            if (i < Bullets.Count) Bullets[i] = text;
        }
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
