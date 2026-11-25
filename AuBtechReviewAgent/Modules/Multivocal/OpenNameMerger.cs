using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// One name per thing for the open attributes of the map (MLR block J, #249): the sources name tools in their own
/// spelling, so "MemGPT" and "Letta (MemGPT)" were counted as two tools in a live run. A value that lists several names
/// is first split into them, and for an attribute about tools a value that is not a tool is left out, by the rules of
/// <see cref="ToolNameRules"/> (#466). Names are merged in code only where these rules match: case, punctuation,
/// spaces, hyphens, underscores and a trailing generic word ("server", "-based") are ignored, and "X (Y)" matches both
/// X and Y. Nothing else is merged, so "Agent" and "Agent Skills" stay apart. A value that is the name of one of the
/// review's own included sources is not counted as a tool. Every split, every merge and every value left out is
/// written to the extraction file, which is in the archive.
/// </summary>
public static class OpenNameMerger
{
    private static readonly Regex Bracketed = new(@"^(?<outer>[^()]+?)\s*\((?<inner>[^()]+)\)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The keys a name matches on: the name without case, punctuation, spaces or a trailing generic word, and for
    /// "X (Y)" both X and Y.
    /// </summary>
    public static IReadOnlyCollection<string> Keys(string name)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string text = ToolNameRules.WithoutGenericTail(name ?? "");
        var match = Bracketed.Match(text);
        if (match.Success)
        {
            Add(keys, ToolNameRules.WithoutGenericTail(match.Groups["outer"].Value));
            Add(keys, ToolNameRules.WithoutGenericTail(match.Groups["inner"].Value));
        }
        else Add(keys, text);
        return keys;
    }

    /// <summary>The keys of a source's own name: its title, and for a code repository ("owner/name") the name alone.</summary>
    public static IReadOnlyCollection<string> SourceKeys(string title)
    {
        string text = title ?? "";
        var keys = new HashSet<string>(Keys(text), StringComparer.Ordinal);
        int slash = text.LastIndexOf('/');
        if (slash >= 0 && slash < text.Length - 1) keys.UnionWith(Keys(text[(slash + 1)..]));
        return keys;
    }

    private static void Add(HashSet<string> keys, string part)
    {
        string key = ToolNameRules.Key(part);
        if (key.Length > 0) keys.Add(key);
    }

    /// <summary>
    /// Splits the values that list several names, leaves out the values that name an included source and, for an
    /// attribute about tools, the ones that are not a tool, merges the names of every open attribute across the
    /// extracted sources, records all of it on <paramref name="file"/> and works out each source's coverage again.
    /// </summary>
    public static void Apply(MultivocalExtractionFile file, MapVersion map, MultivocalPlan plan)
    {
        file.NameRules = ToolNameRules.Version;
        var ownKeys = file.Sources.Where(s => s.Error == null)
            .SelectMany(s => SourceKeys(s.Title).Select(k => (Key: k, s.Title)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);

        foreach (var attribute in map.Attributes.Where(a => a.Open))
        {
            var slots = file.Sources.Where(s => s.Error == null)
                .Select(s => s.Attributes.FirstOrDefault(a => a.Attribute == attribute.Name)).Where(a => a != null).Select(a => a!).ToList();

            if (attribute.Multiple)
                foreach (var slot in slots) SplitValues(file, attribute.Name, slot);

            foreach (var slot in slots)
                foreach (var value in slot.Values.ToList())
                    if (Keys(value.Value).FirstOrDefault(ownKeys.ContainsKey) is { } key)
                    {
                        slot.Values.Remove(value);
                        slot.OwnSource.Add(value);
                        file.OwnSourceValues.Add(new OwnSourceValue { Attribute = attribute.Name, Value = value.Value, Source = ownKeys[key] });
                    }

            if (ToolNameRules.IsToolAttribute(attribute))
                foreach (var slot in slots)
                    foreach (var value in slot.Values.ToList())
                        if (ToolNameRules.NotATool(value.Value) is { } reason)
                        {
                            slot.Values.Remove(value);
                            slot.NotATool.Add(value);
                            file.NotToolValues.Add(new NotToolValue { Attribute = attribute.Name, Value = value.Value, Reason = reason });
                        }

            foreach (var group in Groups(slots.SelectMany(s => s.Values.Select(v => v.Value))))
            {
                string name = Preferred(group, slots);
                foreach (var slot in slots)
                {
                    foreach (var value in slot.Values.Where(v => group.Contains(v.Value)))
                        value.Value = name;
                    // A source that used two spellings of one name keeps the name once, with its first quote.
                    slot.Values = slot.Values.GroupBy(v => v.Value, StringComparer.Ordinal).Select(g => g.First()).ToList();
                }
                file.NameMerges.Add(new NameMerge { Attribute = attribute.Name, Name = name, Merged = group.Where(n => n != name).OrderBy(n => n, StringComparer.Ordinal).ToList() });
            }
        }
        foreach (var source in file.Sources.Where(s => s.Error == null))
            source.Coverage = MultivocalExtractor.CoverageOf(source, map, plan);
    }

    /// <summary>Replaces each value that lists several names with one value per name, each with the value's quote.</summary>
    private static void SplitValues(MultivocalExtractionFile file, string attribute, ExtractedAttribute slot)
    {
        var values = new List<ExtractedValue>();
        foreach (var value in slot.Values)
        {
            var parts = ToolNameRules.Split(value.Value);
            if (parts.Count > 1) file.SplitValues.Add(new SplitValue { Attribute = attribute, Value = value.Value, Parts = parts.ToList() });
            values.AddRange(parts.Select(p => new ExtractedValue { Value = p, Quote = value.Quote }));
        }
        slot.Values = values.GroupBy(v => v.Value, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// What was merged and left out, in plain words for the reader (the Map tab, the data appendix and the method), for
    /// one attribute or for all; null when nothing was.
    /// </summary>
    public static string? Summary(MultivocalExtractionFile file, string? attribute = null)
    {
        var merges = file.NameMerges.Where(m => attribute == null || m.Attribute == attribute).ToList();
        var own = file.OwnSourceValues.Where(o => attribute == null || o.Attribute == attribute).ToList();
        var splits = file.SplitValues.Where(s => attribute == null || s.Attribute == attribute).ToList();
        var notTools = file.NotToolValues.Where(n => attribute == null || n.Attribute == attribute).ToList();
        var parts = new List<string>();
        if (splits.Count > 0)
            parts.Add("Values that named several things, split in code: " + string.Join("; ", splits.GroupBy(s => s.Value, StringComparer.Ordinal).Select(g => $"\"{g.Key}\" into {string.Join(", ", g.First().Parts.Select(n => $"\"{n}\""))}")) + ".");
        if (merges.Count > 0)
            parts.Add("Names merged in code as the same tool or thing: " + string.Join("; ", merges.Select(m => $"\"{m.Name}\" for {string.Join(", ", m.Merged.Select(n => $"\"{n}\""))}")) + ".");
        if (notTools.Count > 0)
            parts.Add("Not counted as tools, by the rules in code: " + string.Join("; ", notTools.GroupBy(n => n.Reason, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{Plural(g.Key)} {string.Join(", ", g.Select(n => $"\"{n.Value}\"").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}")) + ".");
        if (own.Count > 0)
            parts.Add("Not counted, because they name one of the review's own sources: " + string.Join("; ", own.GroupBy(o => (o.Value, o.Source)).Select(g => $"\"{g.Key.Value}\" ({g.Key.Source})")) + ".");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string Plural(string reason) => reason == ToolNameRules.WebAddress ? "web addresses" : reason + "s";

    /// <summary>The spellings that match each other, directly or through a shared bracketed name; only groups of two or more.</summary>
    private static List<HashSet<string>> Groups(IEnumerable<string> names)
    {
        var groups = new List<(HashSet<string> Names, HashSet<string> Keys)>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var keys = Keys(name);
            var touching = groups.Where(g => g.Keys.Overlaps(keys)).ToList();
            var merged = (Names: new HashSet<string>(StringComparer.Ordinal) { name }, Keys: new HashSet<string>(keys, StringComparer.Ordinal));
            foreach (var g in touching)
            {
                merged.Names.UnionWith(g.Names);
                merged.Keys.UnionWith(g.Keys);
                groups.Remove(g);
            }
            groups.Add(merged);
        }
        return groups.Where(g => g.Names.Count > 1).Select(g => g.Names).ToList();
    }

    /// <summary>The spelling most sources use; on a tie the one with fewer hyphens and underscores, then the longer, then by order.</summary>
    private static string Preferred(HashSet<string> group, List<ExtractedAttribute> slots) => group
        .OrderByDescending(n => slots.Count(s => s.Values.Any(v => v.Value == n)))
        .ThenBy(n => n.Count(c => c is '-' or '_'))
        .ThenByDescending(n => n.Length)
        .ThenBy(n => n, StringComparer.Ordinal)
        .First();
}
