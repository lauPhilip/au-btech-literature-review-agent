using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Repairs the Mermaid flowcharts the model writes before they are stored and rendered. The model puts
/// citation markers and punctuation inside node labels, e.g. <c>B1[Cognitive MAS for Cloud Automation [1]]</c>
/// or <c>B2[OS-Layer Recovery (+20%) [4]]</c>. Unquoted, the inner brackets end the label early and Mermaid
/// shows "Syntax error in text". Quoting every node and edge label (<c>B1["... [1]"]</c>) is valid Mermaid
/// for any label content, so each label is wrapped in quotes and any quotes inside it are escaped.
/// </summary>
public static class MermaidSanitizer
{
    private static readonly string[] PassThroughPrefixes =
        { "graph", "flowchart", "subgraph", "end", "style", "classDef", "class ", "linkStyle", "click", "%%", "direction" };

    // Edge operators, optionally followed by a |label|. Longest first so "-.->" wins over "-.".
    private static readonly Regex EdgePattern = new(
        @"\s*(<-->|-\.->|==>|-->|---|===|--x|--o|-\.-)\s*(\|[^|]*\|)?\s*",
        RegexOptions.Compiled);

    // A node: id followed by a shape with a label. Openers/closers are paired explicitly; the label is
    // greedy so a label containing "]" or ")" still runs to the shape's real closing bracket.
    private static readonly (string Open, string Close)[] Shapes =
    {
        ("[[", "]]"), ("[(", ")]"), ("((", "))"), ("([", "])"), ("{{", "}}"),
        ("[/", "/]"), ("[\\", "\\]"), ("[", "]"), ("(", ")"), ("{", "}"), (">", "]"),
    };

    /// <summary>
    /// Returns cleaned Mermaid source, or an empty string when the text does not look like a flowchart at
    /// all (so the page can skip the preview instead of showing an error).
    /// </summary>
    public static string Sanitize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        string text = source.Replace("```mermaid", "").Replace("```", "").Replace("\r", "").Trim();

        var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return string.Empty;
        if (!Regex.IsMatch(lines[0].Trim(), @"^(graph|flowchart)\b", RegexOptions.IgnoreCase))
        {
            // Only flowcharts are repaired; anything else is left alone if it has a known header.
            return Regex.IsMatch(lines[0].Trim(), @"^(sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|mindmap|timeline)\b")
                ? string.Join("\n", lines)
                : string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(lines[0].Trim());
        foreach (var raw in lines.Skip(1))
        {
            string line = raw.Trim();
            if (PassThroughPrefixes.Any(p => line.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine("    " + line);
                continue;
            }
            sb.AppendLine("    " + SanitizeStatement(line.TrimEnd(';')));
        }
        return sb.ToString().TrimEnd();
    }

    private static string SanitizeStatement(string line)
    {
        var parts = new List<string>();
        int pos = 0;
        foreach (Match m in EdgePattern.Matches(line))
        {
            // Skip operators that sit inside a label (e.g. "A[x --> y]"): only split where brackets are balanced.
            if (!IsBalanced(line.Substring(pos, m.Index - pos))) continue;
            parts.Add(SanitizeNode(line.Substring(pos, m.Index - pos)));
            string label = m.Groups[2].Success ? QuoteEdgeLabel(m.Groups[2].Value) : "";
            parts.Add($" {m.Groups[1].Value}{label} ");
            pos = m.Index + m.Length;
        }
        parts.Add(SanitizeNode(line.Substring(pos)));
        return string.Concat(parts).Trim();
    }

    private static bool IsBalanced(string segment)
    {
        int depth = 0;
        foreach (char c in segment)
        {
            if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth--;
        }
        return depth == 0;
    }

    private static string SanitizeNode(string segment)
    {
        string s = segment.Trim();
        if (s.Length == 0) return s;

        // "A & B" chains several nodes; sanitise each one.
        if (s.Contains(" & ") && IsBalanced(s))
        {
            var pieces = SplitTopLevel(s, " & ");
            if (pieces.Count > 1) return string.Join(" & ", pieces.Select(SanitizeNode));
        }

        var idMatch = Regex.Match(s, @"^([A-Za-z0-9_\-\.]+)");
        if (!idMatch.Success) return s;
        string id = idMatch.Value;
        string rest = s.Substring(id.Length).TrimStart();
        if (rest.Length == 0) return SafeId(id);

        foreach (var (open, close) in Shapes)
        {
            if (!rest.StartsWith(open, StringComparison.Ordinal) || !rest.EndsWith(close, StringComparison.Ordinal)) continue;
            if (rest.Length < open.Length + close.Length) continue;
            string label = rest.Substring(open.Length, rest.Length - open.Length - close.Length).Trim();
            if (label.StartsWith('"') && label.EndsWith('"') && label.Length >= 2) label = label[1..^1];
            return $"{SafeId(id)}{open}\"{EscapeLabel(label)}\"{close}";
        }
        return s; // unknown shape: leave as written
    }

    private static List<string> SplitTopLevel(string s, string separator)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, separator, 0, separator.Length) == 0)
            {
                result.Add(s[start..i]);
                start = i + separator.Length;
                i += separator.Length - 1;
            }
        }
        result.Add(s[start..]);
        return result;
    }

    // "end" (any case) is a Mermaid keyword and cannot be a node id.
    private static string SafeId(string id) => id.Equals("end", StringComparison.OrdinalIgnoreCase) ? id + "_" : id;

    private static string QuoteEdgeLabel(string pipeLabel)
    {
        string inner = pipeLabel.Trim('|').Trim();
        if (inner.StartsWith('"') && inner.EndsWith('"') && inner.Length >= 2) inner = inner[1..^1];
        return $"|\"{EscapeLabel(inner)}\"|";
    }

    private static string EscapeLabel(string label) =>
        Regex.Replace(label.Replace("\"", "#quot;"), @"\s+", " ").Trim();
}
