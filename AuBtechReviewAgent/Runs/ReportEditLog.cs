using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>
/// One change the reviewer made to a sentence of the finished report, as kept in report-edits.json. Action is
/// "edit" (the reviewer wrote the sentence and its citations were checked again) or "undo" (the sentence went back
/// to what it said before; <see cref="Undoes"/> names the edit that was taken back). SentenceIndex counts the
/// sentences of the field as the run wrote it, as citation-audit.json does. Checks holds a verdict with its quote for
/// every citation in <see cref="After"/>: for an undo, the verdicts the restored text had (none when it is the run's
/// own text again, whose verdicts are in citation-audit.json).
/// </summary>
public sealed record ReportEdit(
    int Number, string Action, string Field, int SentenceIndex, string Before, string After, DateTime EditedUtc,
    string Editor, List<CitationSupportResult> Checks, string Model, string AppVersion, int? Undoes = null)
{
    public const string Edited = "edit";
    public const string Undone = "undo";
}

/// <summary>
/// Every edit of a run's report, in order, as report-edits.json in the run folder (part of the archive). Nothing is
/// ever taken out: an undo is one more entry, so the file is the full history of who changed what, when, and how it
/// was checked.
/// </summary>
public sealed class ReportEditLog
{
    public const string FileName = "report-edits.json";

    public List<ReportEdit> Edits { get; set; } = new();

    private static ILogger _log => AppLog.For<ReportEditLog>();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The run's edits, or an empty log when there are none (or the file cannot be read).</summary>
    public static ReportEditLog Load(string runFolder)
    {
        string path = Path.Join(runFolder, FileName);
        if (!File.Exists(path)) return new ReportEditLog();
        try { return JsonSerializer.Deserialize<ReportEditLog>(File.ReadAllText(path)) ?? new ReportEditLog(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("The report edits could not be read: {Message}", ex.Message);
            return new ReportEditLog();
        }
    }

    public System.Threading.Tasks.Task SaveAsync(string runFolder) =>
        SafeFile.WriteAllTextAsync(Path.Join(runFolder, FileName), JsonSerializer.Serialize(this, Indented));

    /// <summary>
    /// The edits still in force for one sentence, oldest first: every edit adds one, every undo takes the newest away.
    /// The last one is what the sentence says now; with none it says what the run wrote.
    /// </summary>
    public List<ReportEdit> Stack(string field, int sentenceIndex)
    {
        var stack = new List<ReportEdit>();
        foreach (var e in Edits.Where(e => e.Field == field && e.SentenceIndex == sentenceIndex))
        {
            if (e.Action == ReportEdit.Edited) stack.Add(e);
            else if (e.Action == ReportEdit.Undone && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
        }
        return stack;
    }

    /// <summary>The edit each changed sentence shows now; a sentence back to the run's own text is not in it.</summary>
    public Dictionary<(string Field, int SentenceIndex), ReportEdit> Current()
    {
        var current = new Dictionary<(string Field, int SentenceIndex), ReportEdit>();
        foreach (var key in Edits.Select(e => (e.Field, e.SentenceIndex)).Distinct())
            if (Stack(key.Field, key.SentenceIndex) is { Count: > 0 } stack) current[key] = stack[^1];
        return current;
    }

    /// <summary>
    /// What the edits did, for the report's citation-check paragraph and the statement on AI use, written in code
    /// from this file. Empty when no sentence is edited now.
    /// </summary>
    public string Sentence()
    {
        var current = Current().Values.ToList();
        if (current.Count == 0) return "";
        var inv = CultureInfo.InvariantCulture;
        var checks = current.SelectMany(e => e.Checks).ToList();
        string edited = current.Count == 1 ? "1 sentence" : string.Create(inv, $"{current.Count} sentences");
        string text = $"After the run, the reviewer edited {edited} of the generated text. ";
        if (checks.Count == 0)
            text += current.Count == 1 ? "It cites no study, so there was nothing to check again. " : "They cite no study, so there was nothing to check again. ";
        else
        {
            int supported = checks.Count(c => c.Verdict == CitationSupportChecker.Supported);
            int partly = checks.Count(c => c.Verdict == CitationSupportChecker.Partial);
            int not = checks.Count(c => c.Verdict == CitationSupportChecker.NotSupported);
            int other = checks.Count - supported - partly - not;
            text += string.Create(inv, $"The {(checks.Count == 1 ? "citation" : $"{checks.Count} citations")} in {(current.Count == 1 ? "it" : "them")} {(checks.Count == 1 ? "was" : "were")} checked again against the source text kept from the run, with the same check as the generated text: ");
            text += string.Create(inv, $"{supported} supported, {partly} partly supported and {not} not supported");
            text += other > 0 ? string.Create(inv, $", and {other} could not be checked. ") : ". ";
            if (partly + not > 0) text += "Citations that are not fully supported are kept and marked, as in the generated text. ";
        }
        return text + $"Every edit, with the text before and after, the verdicts and their quotes, is listed in {FileName}.";
    }
}
