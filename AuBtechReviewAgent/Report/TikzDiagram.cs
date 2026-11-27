using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// A diagram the model wrote in TikZ (a [TIKZ_START] block of the first report draft), checked in code before any of it
/// reaches main.tex (issue #538, rule 1 of AGENTS.md). Only boxes and the arrows between them are kept: an id, a label,
/// a place, and which box an arrow joins. The picture, its styles and the arrows are written again in code, and every
/// label goes through <see cref="PaperLatex.Escape"/>. A command that reads or writes a file, defines a macro or
/// changes how TeX reads characters, or anything else that is not a box or an arrow, rejects the whole diagram, which
/// is then left out of the paper.
/// </summary>
public sealed class TikzDiagram
{
    /// <summary>The audit file of the check, in the run folder (rule 3).</summary>
    public const string FileName = "diagram-check.json";
    public const int MaxNodes = 30, MaxEdges = 60, MaxLabel = 160, MaxCode = 8000;

    public sealed record Node(string Id, IReadOnlyList<string> Lines, string? Place, string? Of, double? X, double? Y);
    public sealed record Edge(string From, string To, string Arrow, string Connector, string? Label, bool Dashed);

    public List<Node> Nodes { get; } = new();
    public List<Edge> Edges { get; } = new();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const string Id = @"[A-Za-z][A-Za-z0-9_-]{0,31}";
    private const string Opts = @"\[(?<opts>(?:[^\[\]{}]|\{[^{}]*\}|\[[^\[\]{}]*\])*)\]";

    // Commands that read or write files, run programs, define or redefine macros or change how characters are read.
    // The grammar below rejects them too; naming them here puts the reason on file.
    private static readonly Regex Forbidden = new(@"\\(input|include|InputIfFileExists|IfFileExists|openin|openout|read|readline|write|immediate|" +
        @"catcode|lccode|uccode|def|edef|gdef|xdef|let|futurelet|newcommand|renewcommand|providecommand|DeclareRobustCommand|csname|endcsname|" +
        @"expandafter|noexpand|scantokens|usepackage|RequirePackage|documentclass|directlua|latelua|special|pdfshellescape|ShellEscape|" +
        @"newwrite|newread|jobname|makeatletter|tikzset|pgfkeys|pgfmathsetmacro|foreach|usetikzlibrary|pgfplotstableread|string|" +
        @"meaning|lowercase|uppercase|errmessage|typeout|message|batchmode|nonstopmode|loop)(?![A-Za-z])", Options, Timeout);
    private static readonly Regex Comment = new(@"(?<!\\)%[^\n]*", Options, Timeout);
    private static readonly Regex Begin = new(@"^\s*\\begin\{tikzpicture\}\s*(?:" + Opts + @")?", Options, Timeout);
    private static readonly Regex End = new(@"\\end\{tikzpicture\}\s*$", Options, Timeout);
    private static readonly Regex NodeStatement = new(@"^\\node\s*(?:" + Opts + @")?\s*\((?<id>" + Id + @")\)\s*" +
        @"(?:at\s*\(\s*(?<x>-?\d{1,3}(?:\.\d{1,3})?)\s*,\s*(?<y>-?\d{1,3}(?:\.\d{1,3})?)\s*\)\s*)?\{(?<label>[^{}]*)\}$", Options, Timeout);
    private static readonly Regex DrawStart = new(@"^\\(?:draw|path)\s*(?:" + Opts + @")?\s*\((?<id>" + Id + @")\)", Options, Timeout);
    private static readonly Regex Step = new(@"\G\s*(?<conn>--|-\||\|-|->|edge\s*(?:" + Opts + @")?)\s*" +
        @"(?:node\s*(?:\[(?:[^\[\]{}]|\{[^{}]*\})*\])?\s*\{(?<label>[^{}]*)\}\s*)?\((?<id>" + Id + @")\)", Options, Timeout);
    private static readonly Regex Placement = new(@"^(?<place>above left|above right|below left|below right|above|below|left|right)\s*=\s*" +
        @"(?:-?\d+(?:\.\d+)?\s*(?:cm|mm|pt|em)\s*)?of\s+(?<of>" + Id + ")$", Options, Timeout);

    /// <summary>The diagram in a model's code, or the reason it was rejected. Pure: nothing is written.</summary>
    public static (TikzDiagram? Diagram, string? Problem) Check(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (null, "the block is empty");
        if (code.Length > MaxCode) return (null, $"the block is longer than {MaxCode} characters");
        // "-->" is not a TikZ arrow tip; some answers hold a mangled "-¿" for "->".
        code = code.Replace("-¿", "->").Replace("\r", "");
        if (code.Contains("^^", StringComparison.Ordinal)) return (null, "it holds a ^^ character code");
        if (Forbidden.Match(code) is { Success: true } bad) return (null, $"it holds the command \\{bad.Groups[1].Value}");
        code = Comment.Replace(code, "");
        code = End.Replace(Begin.Replace(code, "", 1), "");

        var diagram = new TikzDiagram();
        var statements = Statements(code, out string? unbalanced);
        foreach (string statement in statements)
        {
            string? problem = diagram.Add(statement);
            if (problem != null) return (null, problem);
        }
        if (unbalanced != null) return (null, unbalanced);
        return diagram.Problem() is string p ? (null, p) : (diagram, null);
    }

    // The statements, split at ";" outside braces and brackets.
    private static List<string> Statements(string code, out string? problem)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        problem = null;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            if (depth < 0) { problem = "its braces do not balance"; return list; }
            if (c == ';' && depth == 0) { list.Add(code[start..i].Trim()); start = i + 1; }
        }
        if (depth != 0) problem = "its braces do not balance";
        if (code[start..].Trim().Length > 0) problem ??= "a statement does not end with \";\"";
        return list.Where(s => s.Length > 0).ToList();
    }

    private string? Add(string statement)
    {
        if (NodeStatement.Match(statement) is { Success: true } n)
        {
            var lines = n.Groups["label"].Value.Split(@"\\").Select(l => Unescape(l).Trim()).ToList();
            if (lines.Any(l => l.Contains('\\'))) return "a label holds a command";
            if (lines.Sum(l => l.Length) > MaxLabel) return $"a label is longer than {MaxLabel} characters";
            string? place = null, of = null;
            foreach (string option in SplitOptions(n.Groups["opts"].Value))
                if (Placement.Match(option) is { Success: true } p) (place, of) = (p.Groups["place"].Value, p.Groups["of"].Value);
            double? x = n.Groups["x"].Success ? double.Parse(n.Groups["x"].Value, CultureInfo.InvariantCulture) : null;
            double? y = n.Groups["y"].Success ? double.Parse(n.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
            if (Nodes.Any(o => o.Id == n.Groups["id"].Value)) return "two boxes have the same name";
            Nodes.Add(new Node(n.Groups["id"].Value, lines, place, of, x, y));
            return null;
        }
        if (DrawStart.Match(statement) is { Success: true } d)
        {
            string opts = d.Groups["opts"].Value;
            string from = d.Groups["id"].Value;
            int at = d.Length, steps = 0;
            for (var m = Step.Match(statement, at); m.Success; m = Step.Match(statement, at))
            {
                string conn = m.Groups["conn"].Value;
                string arrowOpts = conn.StartsWith("edge", StringComparison.Ordinal) ? m.Groups["opts"].Value : opts;
                string label = Unescape(m.Groups["label"].Value).Trim();
                if (label.Contains('\\')) return "an arrow's label holds a command";
                if (label.Length > MaxLabel) return $"a label is longer than {MaxLabel} characters";
                string arrow = conn == "->" ? "->" : ArrowOf(arrowOpts);
                string connector = conn is "-|" or "|-" ? conn : "--";
                Edges.Add(new Edge(from, m.Groups["id"].Value, arrow, connector, label.Length > 0 ? label : null, arrowOpts.Contains("dashed", StringComparison.Ordinal)));
                from = m.Groups["id"].Value;
                at = m.Index + m.Length;
                steps++;
            }
            if (steps == 0 || statement[at..].Trim().Length > 0) return "an arrow is not drawn from box to box";
            return null;
        }
        return "a statement is neither a box nor an arrow";
    }

    // The characters a label may escape itself; they are escaped again, with the rest, by PaperLatex.Escape.
    private static string Unescape(string label) =>
        label.Replace(@"\%", "%").Replace(@"\&", "&").Replace(@"\_", "_").Replace(@"\#", "#").Replace(@"\$", "$");

    private static IEnumerable<string> SplitOptions(string options) => options.Split(',').Select(o => o.Trim()).Where(o => o.Length > 0);

    private static string ArrowOf(string options)
    {
        if (options.Contains("<->", StringComparison.Ordinal)) return "<->";
        if (options.Contains("<-", StringComparison.Ordinal)) return "<-";
        return Regex.IsMatch(options, @"(^|[\s,])-(>|\{|Stealth|stealth|latex|Latex)", Options, Timeout) ? "->" : "-";
    }

    private string? Problem()
    {
        if (Nodes.Count == 0) return "it has no boxes";
        if (Nodes.Count > MaxNodes) return $"it has more than {MaxNodes} boxes";
        if (Edges.Count > MaxEdges) return $"it has more than {MaxEdges} arrows";
        var ids = Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        if (Edges.Any(e => !ids.Contains(e.From) || !ids.Contains(e.To))) return "an arrow joins a box that is not drawn";
        // A box placed next to another must come after it, as TikZ reads them in order.
        for (int i = 0; i < Nodes.Count; i++)
            if (Nodes[i].Of is string of && !Nodes.Take(i).Any(n => n.Id == of)) return "a box is placed next to a box drawn after it";
        return null;
    }

    /// <summary>The picture, written in code: fixed styles, the boxes in order, then the arrows. Numbers are invariant.</summary>
    public string ToLatex()
    {
        static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine(@"\begin{tikzpicture}[node distance=0.8cm and 1.2cm, font=\scriptsize,");
        sb.AppendLine(@"  box/.style={draw, align=center, text width=3cm, inner sep=3pt},");
        sb.AppendLine(@"  link/.style={draw, thick}]");
        string? previous = null;
        foreach (var n in Nodes)
        {
            string label = string.Join(@"\\ ", n.Lines.Where(l => l.Length > 0).Select(PaperLatex.Escape));
            string where = n.X is double x && n.Y is double y ? $" at ({Num(x)},{Num(y)})" : "";
            string place = n.Of != null ? $", {n.Place}=of {n.Of}" : where.Length == 0 && previous != null ? $", right=of {previous}" : "";
            sb.AppendLine($"\\node[box{place}] ({n.Id}){where} {{{label}}};");
            previous = n.Id;
        }
        foreach (var e in Edges)
        {
            string label = e.Label is null ? "" : $" node[midway, fill=white, font=\\tiny, align=center] {{{PaperLatex.Escape(e.Label)}}}";
            string style = e.Arrow == "-" ? "link" : $"link, {e.Arrow}";
            if (e.Dashed) style += ", dashed";
            sb.AppendLine($"\\draw[{style}] ({e.From}) {e.Connector}{label} ({e.To});");
        }
        sb.AppendLine(@"\end{tikzpicture}");
        return sb.ToString();
    }

    private static readonly Regex Block = new(@"\[TIKZ_START\](.*?)\[TIKZ_END\]", RegexOptions.Singleline | Options, Timeout);

    /// <summary>The code of the first [TIKZ_START] block in a text, or null.</summary>
    public static string? BlockIn(string? text) => Block.Match(text ?? "") is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /// <summary>
    /// The model's block when its diagram passes the check, otherwise "". The decision, with the reason for a rejection,
    /// is written to <see cref="FileName"/> in the run folder whenever the model wrote a block (rule 3).
    /// </summary>
    public static string Checked(string? block, string runFolder)
    {
        string? code = BlockIn(block);
        if (code == null) return "";
        var (diagram, problem) = Check(code);
        var record = new
        {
            Accepted = diagram != null,
            Problem = problem,
            Boxes = diagram?.Nodes.Count ?? 0,
            Arrows = diagram?.Edges.Count ?? 0,
            Decision = diagram != null
                ? "The model's diagram was kept as boxes and arrows; main.tex draws it again in code with escaped labels."
                : "The model's diagram was left out of the paper, because it did not pass the check in code.",
        };
        Directory.CreateDirectory(runFolder);
        File.WriteAllText(Path.Join(runFolder, FileName), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        return diagram != null ? block! : "";
    }
}
