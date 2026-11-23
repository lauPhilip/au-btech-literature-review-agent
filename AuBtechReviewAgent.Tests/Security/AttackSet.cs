using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>One poisoned source of the attack set (#373) and what the defences must do with it; see AttackSet/manifest.json.</summary>
public sealed record AttackCase(string Id, string Kind, string Title, string Trick, List<string> Flags, int Invisible, int HiddenElements,
    List<string> NotToModel, string Decision, string? Gap = null, string? ReachesModel = null, string? Kept = null)
{
    /// <summary>The paper's text or the page's HTML, with its invisible characters written out as the real ones.</summary>
    public string Text { get; init; } = "";

    public bool IsPage => Kind == "page";

    public override string ToString() => Id;
}

/// <summary>
/// Reads the attack set from the test sources: the manifest, the papers and the pages. Invisible characters are
/// written as {U+200B} in the data files so a reader sees them, and {TAG:text} spells text in Unicode tag characters.
/// </summary>
public static class AttackSet
{
    private static readonly Lazy<IReadOnlyList<AttackCase>> Loaded = new(Load);

    public static IReadOnlyList<AttackCase> All => Loaded.Value;

    public static IEnumerable<AttackCase> Papers => All.Where(c => !c.IsPage);

    public static IEnumerable<AttackCase> Pages => All.Where(c => c.IsPage);

    public static AttackCase Case(string id) => All.Single(c => c.Id == id);

    /// <summary>Every case id, for a theory with one row per case.</summary>
    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var c in All) data.Add(c.Id);
        return data;
    }

    private static string Folder([CallerFilePath] string here = "") => Path.Join(Path.GetDirectoryName(here)!, "AttackSet");

    private static IReadOnlyList<AttackCase> Load()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(Folder(), "manifest.json")));
        var cases = manifest.RootElement.GetProperty("cases").Deserialize<List<AttackCase>>(options)!;
        var texts = Sections("papers.txt").Concat(Sections("pages.txt")).ToDictionary(s => s.Id, s => s.Text);
        if (texts.Count != cases.Count || cases.Any(c => !texts.ContainsKey(c.Id)))
            throw new InvalidOperationException("Every case in manifest.json needs its text in papers.txt or pages.txt, and only those.");
        return cases.Select(c => c with { Text = texts[c.Id], Kept = c.Kept == null ? null : Decode(c.Kept) }).ToList();
    }

    private static IEnumerable<(string Id, string Text)> Sections(string file)
    {
        string all = File.ReadAllText(Path.Join(Folder(), file)).Replace("\r\n", "\n");
        var heads = Regex.Matches(all, @"^=== (\S+) ===$", RegexOptions.Multiline);
        for (int i = 0; i < heads.Count; i++)
        {
            int start = heads[i].Index + heads[i].Length, end = i + 1 < heads.Count ? heads[i + 1].Index : all.Length;
            yield return (heads[i].Groups[1].Value, Decode(all[start..end].Trim('\n')));
        }
    }

    /// <summary>Turns {U+XXXX} into its character and {TAG:text} into the text spelled in tag characters.</summary>
    public static string Decode(string text)
    {
        text = Regex.Replace(text, @"\{TAG:([^}]*)\}", m => string.Concat(m.Groups[1].Value.Select(c => char.ConvertFromUtf32(0xE0000 + c))));
        return Regex.Replace(text, @"\{U\+([0-9A-F]{4,6})\}", m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
    }
}
