using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// Flags stored by the scan before the strong and weak split (#472, #478). That scan used the names that are now strong
/// for its looser forms, so a paper rebuilt from an older run's files counted every such flag as strong (7 sources instead
/// of 2 in run 50e9a8e3). Flags are now stamped with <see cref="ScanVersion"/>, and older ones are judged again when a
/// paper or report counts them.
/// </summary>
public static partial class PromptSafety
{
    /// <summary>The scan that tells strong phrases from weak mentions; stored with the flags it wrote.</summary>
    public const string ScanVersion = "strong-weak-v1";

    // The names the older scan gave to forms that are now weak, mapped to the weak pattern of the same family.
    private static readonly Dictionary<string, string> LooseFormOf = new()
    {
        ["ignore-instructions"] = "mentions-ignoring",
        ["addresses-the-model"] = "mentions-ai-role",
        ["chat-markup"] = "chat-template",
    };

    /// <summary>
    /// The flags as the current scan judges them. Flags of the current scan are returned as they are. Older ones are
    /// scanned again: the kept text when there is one (keeping a hidden-text flag, whose hidden text is not kept), and
    /// otherwise each flag's phrase, so a flag whose phrase no longer matches its strong pattern becomes that pattern's
    /// weak form. Nothing is written back: the run's files keep what the scan of the day found.
    /// </summary>
    public static List<SourceFlag>? Current(List<SourceFlag>? flags, string? scan, string? keptText = null)
    {
        if (scan == ScanVersion || flags is not { Count: > 0 }) return flags;
        if (keptText != null)
        {
            var again = Find(keptText) ?? new List<SourceFlag>();
            again.AddRange(flags.Where(f => f.Pattern == HiddenText.FlagPattern));
            return again.Count > 0 ? again : null;
        }
        return flags.Select(f => LooseFormOf.TryGetValue(f.Pattern, out var weak) && FirstMatch(f.Pattern, f.Excerpt.Trim('…')) == null
            ? f with { Pattern = weak } : f).ToList();
    }
}
