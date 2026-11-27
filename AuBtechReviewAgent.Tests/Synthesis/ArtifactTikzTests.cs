using System.Globalization;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The concept map in main.tex keeps linked boxes side by side and draws no link through a box (issue #684).</summary>
public class ArtifactTikzTests
{
    // A box is 3.2 cm of text plus 3 pt of padding each side; two lines of \scriptsize stay inside 0.5 cm of its centre.
    private const double HalfWidth = 1.7, HalfHeight = 0.5;

    private static ReviewArtifact Map(string kind, string[] ids, params (string From, string To)[] edges) => new()
    {
        Kind = kind,
        Nodes = ids.Select(id => new ArtifactNode { Id = id, Label = "Box " + id }).ToList(),
        Edges = edges.Select(e => new ArtifactEdge { From = e.From, To = e.To }).ToList(),
    };

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static Dictionary<string, (double X, double Y)> Boxes(string tikz) =>
        Regex.Matches(tikz, @"\\node\[box\] \((\w+)\) at \((-?[\d.]+),(-?[\d.]+)\)")
            .ToDictionary(m => m.Groups[1].Value, m => (D(m.Groups[2].Value), D(m.Groups[3].Value)));

    /// <summary>Every drawn link as its corner points, with node anchors turned into points on the box edge.</summary>
    private static List<(string From, string To, List<(double X, double Y)> Points)> Links(string tikz)
    {
        var boxes = Boxes(tikz);
        (double, double) Point(string token)
        {
            var anchor = Regex.Match(token, @"^\((\w+)\.(east|west|north|south)\)$");
            if (!anchor.Success)
            {
                var xy = Regex.Match(token, @"^\((-?[\d.]+),(-?[\d.]+)\)$");
                return (D(xy.Groups[1].Value), D(xy.Groups[2].Value));
            }
            var (x, y) = boxes[anchor.Groups[1].Value];
            return anchor.Groups[2].Value switch
            {
                "east" => (x + HalfWidth, y), "west" => (x - HalfWidth, y), "north" => (x, y + HalfHeight), _ => (x, y - HalfHeight),
            };
        }
        return tikz.Split('\n').Where(l => l.StartsWith(@"\draw[")).Select(line =>
        {
            var tokens = Regex.Matches(line, @"\((?:\w+\.\w+|-?[\d.]+,-?[\d.]+)\)").Select(m => m.Value).ToList();
            return (Regex.Match(tokens[0], @"\w+").Value, Regex.Match(tokens[^1], @"\w+").Value, tokens.Select(Point).ToList());
        }).ToList();
    }

    private static bool Crosses((double X, double Y) a, (double X, double Y) b, (double X, double Y) box)
    {
        // Sample the segment; a point strictly inside the box (with a small margin) is a crossing.
        for (int i = 0; i <= 200; i++)
        {
            double t = i / 200.0, x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
            if (Math.Abs(x - box.X) < HalfWidth - 0.05 && Math.Abs(y - box.Y) < HalfHeight - 0.05) return true;
        }
        return false;
    }

    private static void AssertNoLinkCrossesABox(string tikz)
    {
        var boxes = Boxes(tikz);
        foreach (var (from, to, points) in Links(tikz))
            for (int i = 1; i < points.Count; i++)
                foreach (var (id, centre) in boxes)
                    Assert.False(Crosses(points[i - 1], points[i], centre), $"The link {from} -> {to} crosses box {id}.");
    }

    [Theory]
    [InlineData(ArtifactKinds.ConceptMap)]
    [InlineData(ArtifactKinds.Flowchart)]
    public void NoLinkIsDrawnThroughABoxInTheRunThatShowedTheFault(string kind)
    {
        // The shape of run 8942b961's map: sixteen boxes in three layers, with links that skip a layer, run back and
        // stay inside one layer.
        var ids = Enumerable.Range(1, 16).Select(i => "n" + i).ToArray();
        var map = Map(kind, ids, ("n1", "n2"), ("n2", "n5"), ("n1", "n5"), ("n3", "n6"), ("n4", "n7"), ("n8", "n9"),
            ("n9", "n16"), ("n8", "n16"), ("n4", "n16"), ("n10", "n11"), ("n12", "n11"), ("n13", "n14"), ("n15", "n14"),
            ("n6", "n3"), ("n7", "n6"));

        string tikz = ArtifactTikz.Draw(map, s => s);

        Assert.Equal(16, Boxes(tikz).Count);
        Assert.Equal(15, Links(tikz).Count);
        AssertNoLinkCrossesABox(tikz);
    }

    [Fact]
    public void ALinkThatSkipsALayerGoesAroundTheBoxes()
    {
        var map = Map(ArtifactKinds.ConceptMap, new[] { "A", "B", "C" }, ("A", "B"), ("B", "C"), ("A", "C"));

        string tikz = ArtifactTikz.Draw(map, s => s);

        var skip = Links(tikz).Single(l => l.From == "A" && l.To == "C");
        Assert.True(skip.Points.Count > 2);
        Assert.True(skip.Points.Max(p => p.Y) > Boxes(tikz).Values.Max(b => b.Y) + HalfHeight);
        Assert.Contains(@"\draw[->, thick] (A.east) -- (B.west);", tikz);
    }

    [Fact]
    public void ABoxSitsAcrossFromTheBoxThatPointsAtIt()
    {
        // P is above Q; Q points at R and P at S. S is listed last, but it is drawn above R.
        var map = Map(ArtifactKinds.ConceptMap, new[] { "P", "Q", "R", "S" }, ("Q", "R"), ("P", "S"));

        var boxes = Boxes(ArtifactTikz.Draw(map, s => s));

        Assert.True(boxes["P"].Y > boxes["Q"].Y);
        Assert.True(boxes["S"].Y > boxes["R"].Y);
    }

    [Fact]
    public void ALoopSitsInNeighbouringLayersAndItsLinkBackIsRouted()
    {
        // Before the fix, A and B pointing at each other pushed B to the far right of a map whose other layers were
        // one or two columns wide.
        var map = Map(ArtifactKinds.ConceptMap, new[] { "A", "B", "C", "D" }, ("A", "B"), ("B", "A"), ("C", "D"));

        string tikz = ArtifactTikz.Draw(map, s => s);

        var boxes = Boxes(tikz);
        Assert.Equal(0.0, boxes["A"].X);
        Assert.Equal(4.4, boxes["B"].X);
        Assert.True(Links(tikz).Single(l => l.From == "B").Points.Count > 2);
        AssertNoLinkCrossesABox(tikz);
    }

    [Fact]
    public void LayersAreCentredAndBoxesAreSquare()
    {
        var map = Map(ArtifactKinds.ConceptMap, new[] { "A", "B", "C", "D" }, ("A", "B"), ("A", "C"), ("A", "D"));

        string tikz = ArtifactTikz.Draw(map, s => s);

        Assert.Equal(new[] { -2.0, 0.0, 2.0 }, Boxes(tikz).Where(b => b.Key != "A").Select(b => b.Value.Y).OrderBy(y => y));
        Assert.Equal(0.0, Boxes(tikz)["A"].Y);
        Assert.DoesNotContain("rounded corners", tikz);
    }
}
