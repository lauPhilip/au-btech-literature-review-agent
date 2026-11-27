using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// Draws a concept map or flowchart as a TikZ picture for main.tex. Boxes sit in layers by their longest path from a
/// starting box (left to right for concept maps, top to bottom for flowcharts). Within a layer, boxes are ordered by
/// where the boxes that point at them sit and are centred, so linked boxes face each other (issue #684). A link
/// between neighbouring layers runs straight across the gap between them; any other link is routed through the gaps
/// and a lane outside the boxes, so no link is drawn through a box. Compiles with plain pdflatex.
/// </summary>
public static class ArtifactTikz
{
    // Box centres: 4.4 cm between concept-map layers and 2.0 cm between boxes in a layer; 2.2 cm between flowchart
    // layers and 4.0 cm between boxes in a row. A box is 3.4 cm wide, so the gap between columns is 1 cm.
    private const double MapStep = 4.4, MapRow = 2.0, ChartStep = 2.2, ChartRow = 4.0;
    // Room between the outermost box centre and the first lane, and between two lanes.
    private const double LaneClearance = 1.2, ChartLaneClearance = 2.3, LaneSpacing = 0.3;

    public static string Draw(ReviewArtifact a, Func<string, string> escape)
    {
        if (!a.IsDiagram || a.Nodes.Count == 0) return "";
        var layers = Layers(a);
        bool vertical = a.Kind == ArtifactKinds.Flowchart;
        double step = vertical ? ChartStep : MapStep, row = vertical ? ChartRow : MapRow;

        var layerOf = new Dictionary<string, int>();
        var across = new Dictionary<string, double>();
        for (int l = 0; l < layers.Count; l++)
            for (int i = 0; i < layers[l].Count; i++)
            {
                layerOf[layers[l][i]] = l;
                across[layers[l][i]] = (i - (layers[l].Count - 1) / 2.0) * row;
            }

        // A point given as (distance along the layers, distance across them), turned into page coordinates.
        string P(double along, double side) => vertical ? $"({F(side)},{F(-along)})" : $"({F(along)},{F(-side)})";

        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[box/.style={draw, align=center, text width=3.2cm, font=\scriptsize, inner sep=3pt}]");
        foreach (var n in a.Nodes)
            sb.AppendLine($"\\node[box] ({n.Id}) at {P(layerOf[n.Id] * step, across[n.Id])} {{{escape(n.Label)}}};");

        string exit = vertical ? "south" : "east", entry = vertical ? "north" : "west";
        // Lanes run on the far side of every box: above a concept map, right of a flowchart.
        double outer = vertical ? across.Values.Max() + ChartLaneClearance : -across.Values.Min() + LaneClearance;
        int routed = 0;
        foreach (var e in a.Edges)
        {
            if (!layerOf.TryGetValue(e.From, out int from) || !layerOf.TryGetValue(e.To, out int to)) continue;
            string label = string.IsNullOrWhiteSpace(e.Label) ? "" : $" node[midway, fill=white, font=\\tiny, align=center] {{{escape(e.Label)}}}";
            string style = e.Check == ArtifactLinks.Reading ? "->, thick, dashed" : "->, thick";
            if (to == from + 1)
            {
                sb.AppendLine($"\\draw[{style}] ({e.From}.{exit}) --{label} ({e.To}.{entry});");
                continue;
            }
            // Out through the gap after the source's layer, along a lane outside the boxes, and in through the gap
            // before the target's layer. Each routed link gets its own lane, and its own place in the gaps.
            double nudge = ((routed % 3) - 1) * 0.2;
            double lane = vertical ? outer + routed * LaneSpacing : -(outer + routed * LaneSpacing);
            double outGap = (from + 0.5) * step + nudge, inGap = (to - 0.5) * step + nudge;
            sb.AppendLine($"\\draw[{style}] ({e.From}.{exit}) -- {P(outGap, across[e.From])} -- {P(outGap, lane)} --{label} "
                + $"{P(inGap, lane)} -- {P(inGap, across[e.To])} -- ({e.To}.{entry});");
            routed++;
        }
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    /// <summary>
    /// The boxes in layers, in drawing order. Layers are numbered by longest path and then placed next to each other
    /// by their order, not their number: a cycle can push the longest path far up, and a gap of empty layers made the
    /// map so wide that it shrank to an unreadable size (issue #126).
    /// </summary>
    internal static List<List<string>> Layers(ReviewArtifact a)
    {
        var known = a.Nodes.Select(n => n.Id).ToHashSet();
        var edges = a.Edges.Where(e => e.From != e.To && known.Contains(e.From) && known.Contains(e.To)).ToList();
        edges.RemoveAll(BackLinks(a, edges).Contains);
        var depth = a.Nodes.ToDictionary(n => n.Id, _ => 0);
        // Longest-path layering; at most Nodes.Count rounds, so a cycle cannot loop forever.
        for (int round = 0; round < a.Nodes.Count; round++)
        {
            bool changed = false;
            foreach (var e in edges)
                if (depth[e.To] < depth[e.From] + 1 && depth[e.From] + 1 < a.Nodes.Count) { depth[e.To] = depth[e.From] + 1; changed = true; }
            if (!changed) break;
        }

        var order = a.Nodes.Select((n, i) => (n.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var layers = a.Nodes.GroupBy(n => depth[n.Id]).OrderBy(g => g.Key).Select(g => g.Select(n => n.Id).ToList()).ToList();
        // One sweep from the first layer: a box goes to the mean place of the boxes in earlier layers that point at it
        // (its barycentre), so links run across rather than up and down. Boxes with no such link keep their place.
        var place = new Dictionary<string, double>();
        for (int l = 0; l < layers.Count; l++)
        {
            double Centre(string id)
            {
                var sources = edges.Where(e => e.To == id && place.ContainsKey(e.From)).Select(e => place[e.From]).ToList();
                return sources.Count > 0 ? sources.Average() : (layers[l].IndexOf(id) - (layers[l].Count - 1) / 2.0);
            }
            var centres = layers[l].ToDictionary(id => id, Centre);
            layers[l] = layers[l].OrderBy(id => centres[id]).ThenBy(id => order[id]).ToList();
            for (int i = 0; i < layers[l].Count; i++) place[layers[l][i]] = i - (layers[l].Count - 1) / 2.0;
        }
        return layers;
    }

    /// <summary>
    /// The links that close a loop, found by a depth-first walk in the order the boxes are listed. They are left out
    /// of the layering, so a loop of two boxes sits in two neighbouring layers instead of climbing to the right, and
    /// are drawn as routed links back.
    /// </summary>
    private static HashSet<ArtifactEdge> BackLinks(ReviewArtifact a, List<ArtifactEdge> edges)
    {
        var back = new HashSet<ArtifactEdge>();
        var state = a.Nodes.ToDictionary(n => n.Id, _ => 0); // 0 not seen, 1 on the current path, 2 done
        foreach (var root in a.Nodes.Select(n => n.Id))
        {
            if (state[root] != 0) continue;
            // An explicit stack, so a long chain cannot overflow the call stack.
            var stack = new Stack<(string Id, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                var (id, next) = stack.Pop();
                var outgoing = edges.Where(e => e.From == id).ToList();
                if (next >= outgoing.Count) { state[id] = 2; continue; }
                stack.Push((id, next + 1));
                var e = outgoing[next];
                if (state[e.To] == 1) back.Add(e);
                else if (state[e.To] == 0) { state[e.To] = 1; stack.Push((e.To, 0)); }
            }
        }
        return back;
    }

    private static string F(double v) => (Math.Abs(v) < 0.05 ? 0 : v).ToString("0.0", CultureInfo.InvariantCulture);
}
