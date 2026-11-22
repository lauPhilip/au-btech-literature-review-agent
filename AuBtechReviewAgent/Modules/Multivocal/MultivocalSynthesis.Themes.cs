using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// One research question's themes: the value groups, the themes the model names, the placement of the groups in
// them and its second, independent check.
public sealed partial class MultivocalSynthesiser
{
    /// <summary>How many value groups are placed in one call: few enough that the model places every one.</summary>
    public const int GroupsPerCall = 30;

    /// <summary>A value group's id ("V12") in the model's text.</summary>
    private static readonly Regex ValueId = new(@"\bV\d+\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// One question, in three steps that each stay small: the model names the themes from the question's value groups;
    /// it places the groups in the themes a batch at a time (every group placed, or a batch that still fails after two
    /// retries is listed as not placed, with a note); and for each theme with claims from two sources or more it names
    /// the tensions, from sample quotes. Values of an open attribute (a tool's name, say) are names, not claims, so they
    /// are never asked about tensions. Every finding of a group goes into its group's themes.
    /// </summary>
    public static async Task SynthesiseQuestionAsync(IChatCompletionService chat, string topic, string question, string questionText,
        List<ValueGroup> groups, List<SynthesisFinding> findings, IReadOnlySet<string> openAttributes, MultivocalSynthesisFile file, IProgress<string>? progress)
    {
        var named = await AskThemesAsync(chat, topic, question, questionText, groups);
        var placement = named.Themes.ToDictionary(t => t.Name.Trim(), _ => new List<ValueGroup>(), StringComparer.OrdinalIgnoreCase);
        var firstPlacement = new Dictionary<string, HashSet<string>>();

        for (int at = 0; at < groups.Count; at += GroupsPerCall)
        {
            var batch = groups.Skip(at).Take(GroupsPerCall).ToList();
            progress?.Report($"{question}: placing values {at + 1}–{at + batch.Count} of {groups.Count}");
            ModelPlacement placed;
            try { placed = await AskPlacementAsync(chat, question, questionText, named, batch); }
            catch (LlmOutputException ex)
            {
                // A batch the model cannot place after two retries is kept on file as not placed, not lost.
                file.Notes.Add($"{question}: {batch.Count} value(s) could not be placed by the model ({ex.Message}).");
                file.Unassigned.AddRange(batch.Select(g => new UnassignedFinding { Finding = g.Label, Reason = "Not placed: the model's answers failed the check." }));
                continue;
            }
            foreach (var p in placed.Placements)
            {
                var group = batch.First(g => g.Id == p.Group.Trim());
                if (p.Themes.Count == 0)
                    file.Unassigned.Add(new UnassignedFinding { Finding = group.Label, Reason = string.IsNullOrWhiteSpace(p.Reason) ? "In no theme." : p.Reason.Trim() });
                foreach (var name in p.Themes) placement[name.Trim()].Add(group);
                firstPlacement[group.Id] = p.Themes.Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        // A second, independent placement with the values in the other order, as the systematic review's second
        // coding: it does not change the themes, and its agreement with the first is reported as Cohen's kappa.
        var secondPlacement = new Dictionary<string, HashSet<string>>();
        string? secondError = null;
        for (int at = 0; at < groups.Count; at += GroupsPerCall)
        {
            var batch = groups.Skip(at).Take(GroupsPerCall).Where(g => firstPlacement.ContainsKey(g.Id)).Reverse().ToList();
            if (batch.Count == 0) continue;
            progress?.Report($"{question}: second placement of values {at + 1}–{at + batch.Count}");
            try
            {
                var again = await AskPlacementAsync(chat, question, questionText, named, batch);
                foreach (var p in again.Placements) secondPlacement[p.Group.Trim()] = p.Themes.Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (LlmOutputException ex)
            {
                secondError = ex.Message;
            }
        }
        var agreement = ComparePlacements(question, named.Themes.Select(t => t.Name.Trim()).ToList(), firstPlacement, secondPlacement);
        agreement.Error = secondError;
        file.SecondPlacements.Add(agreement);

        // Two themes holding exactly the same values say the same thing twice (seen in a live run: two pairs of RQ3
        // themes with the same sources and quotes); the later one is merged into the first, with a note.
        var merged = MergeIdenticalThemes(named.Themes.Select(t => (t.Name.Trim(), placement[t.Name.Trim()].Select(g => g.Id).ToList())).ToList());
        foreach (var (kept, dropped) in merged)
            file.Notes.Add($"{question}: the theme \"{dropped}\" held exactly the same values as \"{kept}\" and was merged into it.");
        var droppedNames = merged.Select(m => m.Dropped).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var theme in named.Themes)
        {
            if (droppedNames.Contains(theme.Name.Trim())) continue;
            var inTheme = placement[theme.Name.Trim()];
            if (inTheme.Count == 0)
            {
                file.Notes.Add($"{question}: the theme \"{theme.Name.Trim()}\" got no values and was left out.");
                continue;
            }
            var result = Count(new SynthesisTheme
            {
                Question = question,
                Name = theme.Name.Trim(),
                Description = theme.Description.Trim(),
                Findings = inTheme.SelectMany(g => g.Findings).Distinct().ToList(),
            }, findings);
            var claims = inTheme.Where(g => !openAttributes.Contains(g.Attribute)).ToList();
            var claimSources = findings.Where(f => claims.Any(g => g.Findings.Contains(f.Id))).Select(f => f.Address).Distinct().Count();
            if (claims.Count >= 2 && claimSources >= 2)
            {
                try
                {
                    var tensions = await AskTensionsAsync(chat, question, questionText, result.Name, claims, findings);
                    result.Tensions = TensionsFor(tensions, claims, findings, out var notes);
                    file.Notes.AddRange(notes.Select(n => $"{question}, \"{result.Name}\": {n}"));
                }
                catch (LlmOutputException ex)
                {
                    // Tensions are an extra: a failed answer leaves the theme without them and says so.
                    file.Notes.Add($"{question}, \"{result.Name}\": the tensions could not be read ({ex.Message}).");
                }
            }
            file.Themes.Add(result);
        }
    }

    /// <summary>
    /// The agreement of two placements of a question's values, over every value placed both times and every theme:
    /// Cohen's kappa, and each value and theme the two placements disagree on.
    /// </summary>
    public static PlacementAgreement ComparePlacements(string question, IReadOnlyList<string> themes,
        IReadOnlyDictionary<string, HashSet<string>> first, IReadOnlyDictionary<string, HashSet<string>> second)
    {
        var result = new PlacementAgreement { Question = question };
        var pairs = new List<(bool, bool)>();
        foreach (var group in first.Keys.Where(second.ContainsKey).OrderBy(g => int.TryParse(g.TrimStart('V'), out int n) ? n : int.MaxValue))
            foreach (var theme in themes)
            {
                bool a = first[group].Contains(theme), b = second[group].Contains(theme);
                pairs.Add((a, b));
                if (a != b) result.Disagreements.Add($"{group}, \"{theme}\": {(a ? "first placement only" : "second placement only")}");
            }
        result.CellsCompared = pairs.Count;
        result.Agreements = pairs.Count(p => p.Item1 == p.Item2);
        result.Kappa = pairs.Count == 0 ? null : Math.Round(ScreeningConfusion.From(pairs).CohensKappa, 3);
        return result;
    }

    /// <summary>
    /// The themes that hold exactly the same values as an earlier theme, each with the earlier theme it is merged into.
    /// Themes without values are left to the caller.
    /// </summary>
    public static List<(string Kept, string Dropped)> MergeIdenticalThemes(IReadOnlyList<(string Name, List<string> Groups)> themes)
    {
        var merged = new List<(string, string)>();
        var seen = new Dictionary<string, string>();
        foreach (var (name, groups) in themes)
        {
            if (groups.Count == 0) continue;
            string key = string.Join(",", groups.Distinct().OrderBy(g => g, StringComparer.Ordinal));
            if (seen.TryGetValue(key, out var kept)) merged.Add((kept, name));
            else seen[key] = name;
        }
        return merged;
    }

    /// <summary>
    /// The findings of one question grouped by attribute and value ("Practice: Retrieval"), in code: the map's values are
    /// already the first codes, so the model groups a few dozen values instead of hundreds of findings.
    /// </summary>
    public static List<ValueGroup> ValueGroups(IReadOnlyList<SynthesisFinding> findings) => findings
        .GroupBy(f => (f.Attribute, Value: f.Value.Trim().ToLowerInvariant()))
        .Select((g, i) => new ValueGroup
        {
            Id = $"V{i + 1}",
            Attribute = g.Key.Attribute,
            Value = g.First().Value.Trim(),
            Findings = g.Select(f => f.Id).ToList(),
            Sources = g.Select(f => f.Address).Distinct().Count(),
            Samples = g.GroupBy(f => f.Address).Select(s => s.First()).Take(2).Select(f => $"\"{Short(f.Quote)}\" ({f.Title})").ToList(),
        })
        .ToList();

    private static string Short(string quote) => quote.Length > 200 ? quote[..200] + "…" : quote;
}
