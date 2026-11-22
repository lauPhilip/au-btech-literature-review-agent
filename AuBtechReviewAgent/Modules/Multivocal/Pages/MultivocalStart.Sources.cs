using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent.Modules.Multivocal.Pages;

// What the Sources found tab shows of each source (its screening decision, quality score, extraction and kept page)
// and what the reviewer can do with it: change a decision, assess a source again, keep a page's text.
public partial class MultivocalStart
{
    internal GreyExtraction? ExtractionOf(string url) =>
        extractions.TryGetValue(MultivocalSearcher.AddressKey(url), out var x) ? x : null;

    internal async Task ReviewAsync(string address, bool include)
    {
        if (planned == null || screening == null || reviewing != null) return;
        reviewing = address;
        screenError = null;
        try
        {
            var entry = await Screener.ReviewAsync(planned.RunId, editKey, address, include, reviewNotes.GetValueOrDefault(address));
            int index = screening.Sources.FindIndex(s => s.Address == address);
            if (index >= 0) screening.Sources[index] = entry;
            decisions[address] = entry;
        }
        catch (InvalidOperationException ex)
        {
            screenError = ex.Message;
        }
        finally
        {
            reviewing = null;
        }
    }

    internal async Task RetryAsync(string address)
    {
        if (planned == null || quality == null || retrying != null) return;
        retrying = address;
        qualityError = null;
        try
        {
            var again = await Quality.RetryAsync(planned.RunId, editKey, address);
            int index = quality.Sources.FindIndex(q => q.Address == address);
            if (index >= 0) quality.Sources[index] = again;
            qualities[address] = again;
            snapshots = Pages.Load(planned.RunId).Pages.ToDictionary(p => MultivocalSearcher.AddressKey(p.Url));
        }
        catch (InvalidOperationException ex)
        {
            qualityError = ex.Message;
        }
        finally
        {
            retrying = null;
        }
    }

    internal GreyQuality? QualityOf(string url) =>
        qualities.TryGetValue(MultivocalSearcher.AddressKey(url), out var q) ? q : null;

    internal static string QualityChip(string outcome) => outcome switch
    {
        "Passed" => "ui-chip border-supported-border bg-supported-fill text-supported",
        "Below threshold" => "ui-chip border-partly-border bg-partly-fill text-partly",
        _ => "ui-chip border-line bg-soft text-muted",
    };

    internal GreyScreening? DecisionOf(string url) =>
        decisions.TryGetValue(MultivocalSearcher.AddressKey(url), out var d) ? d : null;

    internal IEnumerable<FoundGreySource> ShownSources() => sourceFilter switch
    {
        "passed" => ledger!.Sources.Where(f => QualityOf(f.Record.Url)?.Outcome == "Passed"),
        "below" => ledger!.Sources.Where(f => QualityOf(f.Record.Url)?.Outcome == "Below threshold"),
        "unassessed" => ledger!.Sources.Where(f => QualityOf(f.Record.Url)?.Outcome == "Not assessed"),
        "included" => ledger!.Sources.Where(f => DecisionOf(f.Record.Url)?.Decision == "Included"),
        "excluded" => ledger!.Sources.Where(f => DecisionOf(f.Record.Url)?.Decision == "Excluded"),
        "look" => ledger!.Sources.Where(f => DecisionOf(f.Record.Url) is { NeedsLook: true }),
        "duplicates" => ledger!.Sources.Where(f => DecisionOf(f.Record.Url)?.Decision == "Duplicate"),
        _ => ledger!.Sources,
    };

    internal static string DecisionLabel(GreyScreening d) => d.Decision switch
    {
        "Included" => "Included",
        "Excluded" => d.ExclusionReason is { } reason ? $"Excluded: {reason.Replace('-', ' ')}" : "Excluded",
        "Duplicate" => "Duplicate",
        _ => "Not screened",
    };

    // The same comparison as ==. The Sources found tab compares decisions through this, as CodeQL flagged the
    // same comparisons written in its Razor markup as comparisons of identical values.
    internal static bool Differ(string? a, string? b) => !string.Equals(a, b, StringComparison.Ordinal);

    internal static string DecisionChip(string decision) => decision switch
    {
        "Included" => "ui-chip border-supported-border bg-supported-fill text-supported",
        "Excluded" => "ui-chip border-line bg-soft text-muted",
        "Duplicate" => "ui-chip border-line bg-page text-muted",
        _ => "ui-chip border-not-supported-border bg-not-supported-fill text-not-supported",
    };

    internal PageSnapshot? SnapshotOf(string url) =>
        snapshots.TryGetValue(MultivocalSearcher.AddressKey(url), out var snapshot) ? snapshot : null;

    internal async Task KeepPageAsync(string url)
    {
        if (planned == null || !keeping.Add(url)) return;
        keepError = null;
        try
        {
            var snapshot = await Pages.KeepAsync(planned.RunId, editKey, url);
            snapshots[MultivocalSearcher.AddressKey(url)] = snapshot;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            keepError = ex.Message;
            Logger.LogInformation("Keeping a page failed: {Message}", ex.Message.Replace("\r", " ").Replace("\n", " "));
        }
        finally
        {
            keeping.Remove(url);
        }
    }

    internal static string SearchLabel(string key) =>
        GreySourceCatalog.Create(key)?.Name ?? MultivocalGuidelines.SearchLabel(key);

    internal static string KindLabel(string key) =>
        MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == key).Label ?? key;
}
