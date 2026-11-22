using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// Checking every citation of the written paper against the kept page text of the source it cites.
public sealed partial class MultivocalWriter
{
    /// <summary>Checks every citation of a written paper against the kept page texts and repairs the rejected ones once.</summary>
    public async Task<MultivocalPaperFile> CheckAsync(Guid runId, string? editKey, IProgress<string>? progress = null)
    {
        if (!_runs.CanEdit(runId, editKey)) throw new InvalidOperationException("Only the browser that planned this run can check its paper.");
        if (!Running.TryAdd(runId, true)) throw new InvalidOperationException("This run's paper is being checked already.");
        string folder = _runs.FolderOf(runId);
        try
        {
            var header = _runs.LoadHeader(runId) ?? throw new InvalidOperationException("This run could not be found.");
            if (header.Stage is not (StageWritten or StageChecking)) throw new InvalidOperationException("This run's paper has been checked already.");
            var input = _reporter.LoadInput(runId) ?? throw new InvalidOperationException("Synthesise the run first.");
            var file = Load(runId) ?? throw new InvalidOperationException("Write the paper first.");
            await RunHeader.WriteAsync(folder, header with { Stage = StageChecking });

            var audit = await CheckPaperAsync(_chat(), input, file, page => _pages.ReadText(runId, page), _parallelism, progress, new CheckCache(_cache, _model));
            await SafeFile.WriteAllTextAsync(Path.Join(folder, AuditFile), JsonSerializer.Serialize(audit, Json));
            await SafeFile.WriteAllTextAsync(Path.Join(folder, PaperFile), JsonSerializer.Serialize(file, Json));
            await RunHeader.WriteAsync(folder, header with { Stage = StageChecked });
            return file;
        }
        catch
        {
            if (_runs.LoadHeader(runId) is { Stage: StageChecking } current)
                await RunHeader.WriteAsync(folder, current with { Stage = StageWritten });
            throw;
        }
        finally
        {
            Running.TryRemove(runId, out _);
        }
    }

    /// <summary>The field names the check gives the paper's parts.</summary>
    public const string RationaleField = "rationale";
    public const string DiscussionField = "discussion";
    public const string SummaryField = "summary";

    /// <summary>
    /// The check itself, on the paper in place: the prose is cleaned of Markdown, citations outside the reference list
    /// are stripped, and every cited sentence goes through the shared grounding check against the kept page text of
    /// the source it cites. A source whose kept text is missing, or no longer matches its fingerprint, is unverifiable:
    /// its search summary is never used as evidence. The answers in code are not checked; they are counted, not written.
    /// </summary>
    public static async Task<MultivocalCitationAudit> CheckPaperAsync(IChatCompletionService chat, MultivocalReportInput input, MultivocalPaperFile file,
        Func<PageSnapshot, string?> readText, int parallelism, IProgress<string>? progress = null, CheckCache? cache = null)
    {
        var (evidence, _) = MultivocalEvidence.Build(input, readText);
        int referenceCount = evidence.Sources.Count;
        var audit = new MultivocalCitationAudit { ReferenceListSize = referenceCount };

        string Plain(string text, string field)
        {
            var (clean, changes) = ProseCleaner.Clean(text, field);
            audit.FormattingChanges.AddRange(changes);
            return clean;
        }
        var fields = new List<(string Field, string Text)> { (RationaleField, Plain(file.Rationale, RationaleField)) };
        fields.AddRange(file.Results.Where(r => r.Kind == "theme").Select(r => (r.Field, Plain(r.Text, r.Field))));
        fields.Add((DiscussionField, Plain(file.Discussion, DiscussionField)));
        if (file.SummaryItems.Count > 0) fields.Add((SummaryField, string.Join("\n", file.SummaryItems.Select(i => Plain(i, SummaryField)))));
        for (int i = 0; i < fields.Count; i++)
        {
            var validation = CitationValidator.ValidateAndStrip(fields[i].Text, referenceCount, fields[i].Field);
            fields[i] = (fields[i].Field, validation.CleanedText);
            audit.Details.AddRange(validation.Stripped);
        }
        audit.InvalidMarkersStripped = audit.Details.Count;

        // The kept page text only: no summary as a stand-in, and no extraction quotes for a source whose kept text is
        // missing or changed, so such a source is unverifiable rather than judged on what was once read from it.
        var sources = evidence.Sources.Where(s => s.Text != null).Select(s => s.Text! with { Abstract = "" }).ToList();
        var withText = sources.Where(s => s.HasFullText).Select(s => s.ReferenceNumber).ToHashSet();
        var quotes = evidence.VerifiedQuotes().Where(q => withText.Contains(q.Key)).ToDictionary(q => q.Key, q => q.Value);
        var grounding = await Grounding.CheckAndRepairAsync(chat, fields, sources, quotes, referenceCount,
            new GroundingOptions(parallelism, SecondCheck: true, Repair: true, Review: input.Planned.Plan.ReviewKind, Cache: cache),
            checking: count => progress?.Report($"Checking {count} citations against the kept pages"),
            checkProgress: (second, done, total) => progress?.Report($"{(second ? "Second check" : "First check")}: {done} of {total} sources"),
            repairing: count => progress?.Report($"Repairing {count} citations and checking them again"));

        var text = grounding.Fields.ToDictionary(f => f.Field, f => f.Text);
        file.Rationale = text.GetValueOrDefault(RationaleField, file.Rationale);
        foreach (var result in file.Results.Where(r => r.Kind == "theme"))
            result.Text = text.GetValueOrDefault(result.Field, result.Text);
        file.Discussion = text.GetValueOrDefault(DiscussionField, file.Discussion);
        if (text.TryGetValue(SummaryField, out var summary))
        {
            var items = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (items.Count > 0) file.SummaryItems = items;
        }

        var cited = new SortedSet<int>(grounding.Fields.SelectMany(f => ThematicSynthesis.CitedIn(f.Text)).Where(r => r >= 1 && r <= referenceCount));
        audit.SourcesCited = cited.Count;
        for (int reference = 1; reference <= referenceCount; reference++)
        {
            if (cited.Contains(reference)) continue;
            bool inTheme = evidence.Themes.Any(t => t.Sources.Contains(reference));
            audit.NotCited.Add(new UncitedSource(reference, inTheme
                ? "in a theme, but not cited after the fill pass, the peer review and the citation repair"
                : "in no theme the model writes about; its findings answer a question in code or were left out"));
        }
        audit.InitialSupportSummary = grounding.BeforeRepair;
        audit.SupportSummary = CitationSupportSummary.From(grounding.Checks, grounding.Removed.Count);
        audit.Repairs = grounding.Repairs;
        audit.RemovedCitations = grounding.Removed;
        audit.SupportChecks = grounding.Checks;
        file.CheckedUtc = DateTime.UtcNow;
        file.Checks = audit.SupportSummary;
        return audit;
    }
}
