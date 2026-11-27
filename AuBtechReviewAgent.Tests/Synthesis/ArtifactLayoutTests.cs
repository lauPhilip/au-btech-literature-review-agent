using System.Globalization;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The concept map in main.tex places its layers side by side, with no empty columns between them (issue #126).</summary>
public class ArtifactLayoutTests
{
    private static ReviewArtifact Map(string kind, params (string From, string To)[] edges)
    {
        // Three boxes with no arrows, so a loop between two boxes can climb well past the other layers.
        var ids = edges.SelectMany(e => new[] { e.From, e.To }).Concat(new[] { "G", "H", "I" }).Distinct().ToList();
        return new ReviewArtifact
        {
            Kind = kind,
            Nodes = ids.Select(id => new ArtifactNode { Id = id, Label = "Box " + id }).ToList(),
            Edges = edges.Select(e => new ArtifactEdge { From = e.From, To = e.To }).ToList(),
        };
    }

    private static List<double> Coordinates(string tikz, int group) => Regex.Matches(tikz, @"\) at \((-?[\d.]+),(-?[\d.]+)\)")
        .Select(m => double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture)).Distinct().OrderBy(v => v).ToList();

    [Fact]
    public void ACycleDoesNotLeaveEmptyColumnsInAConceptMap()
    {
        // A and B point at each other, so their layers climb as far as the loop allows (5 and 6); C to D and the
        // boxes without arrows sit in layers 0 and 1. Before the fix the map jumped from x = 4.4 to x = 22.0.
        var map = Map(ArtifactKinds.ConceptMap, ("A", "B"), ("B", "A"), ("C", "D"));

        var xs = Coordinates(ArtifactBuilder.ToTikz(map, s => s), 1);

        Assert.Equal(0, xs[0]);
        for (int i = 1; i < xs.Count; i++)
            Assert.Equal(4.4, xs[i] - xs[i - 1], 3);
    }

    [Fact]
    public void AFlowchartStacksItsLayersWithoutGapsToo()
    {
        var chart = Map(ArtifactKinds.Flowchart, ("A", "B"), ("B", "A"), ("C", "D"));

        var ys = Coordinates(ArtifactBuilder.ToTikz(chart, s => s), 2);

        Assert.Equal(0, ys[^1]);
        for (int i = 1; i < ys.Count; i++)
            Assert.Equal(2.2, ys[i] - ys[i - 1], 3);
    }

    [Fact]
    public void AChainKeepsOneColumnPerStep()
    {
        var map = Map(ArtifactKinds.ConceptMap, ("A", "B"), ("B", "C"));

        string tikz = ArtifactBuilder.ToTikz(map, s => s);

        Assert.Contains(@"\node[box] (A) at (0.0,", tikz);
        Assert.Contains(@"\node[box] (B) at (4.4,", tikz);
        Assert.Contains(@"\node[box] (C) at (8.8,", tikz);
    }
}
