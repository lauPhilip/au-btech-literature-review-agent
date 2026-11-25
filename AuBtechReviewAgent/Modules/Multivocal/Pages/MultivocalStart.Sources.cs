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
        "grey" => ledger!.GreyPool,
        "academic" => ledger!.FormalPool,
        _ => ledger!.Sources,
    };

    /// <summary>
    /// The Sources found tab's lead: the stopping rule as applied, and how far the selection has gone. The model is named
    /// only when the file names one, the agreement is given per pool for a review with both, and the quality step is
    /// "next" only while it has not run.
    /// </summary>
    public static string SourcesLead(MultivocalLedger ledger, MultivocalScreeningFile? screening, MultivocalQualityFile? quality)
    {
        if (screening == null) return $"{ledger.StoppingApplied} Not screened yet.";
        static string K(double? k) => k is double v ? v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "not computed";
        string sample = screening.AuditSeed == null ? "" : " on the audit sample";
        string agreement = ledger.HasFormalPool
            ? $"; agreement κ{sample} = {K(screening.KappaGrey)} for grey, {K(screening.KappaFormal)} for academic and {K(screening.Kappa)} for both"
            : screening.Kappa is double k ? $"; agreement κ{sample} = {K(k)}" : "";
        string by = screening.Model.Trim().Length > 0 ? $" by {screening.Model.Trim()}" : "";
        string how = screening.AuditSeed == null ? "Screened twice"
            : $"Screened once, and {MultivocalSecondScreening.Count(screening, null).ScreenedTwice} twice,";
        return $"{ledger.StoppingApplied} {how} with the plan's criteria{by}{agreement}{(quality == null ? "; the quality check comes next." : ".")}";
    }

    /// <summary>
    /// What an academic source's kept text is (decision 8): its full text and where it came from, or its abstract alone;
    /// for a Stack Exchange question, that it came from the API, with its authors (#453). Null for a grey source's page.
    /// </summary>
    public static string? TextBasisLine(PageSnapshot snapshot) => snapshot.TextBasis switch
    {
        null => null,
        { } api when api.StartsWith(MultivocalPages.StackExchangeBasisPrefix, StringComparison.Ordinal) =>
            $"Kept {snapshot.AccessedUtc:yyyy-MM-dd HH:mm} UTC from the {api} (the site refuses automated page requests) · {snapshot.Characters:N0} characters · SHA-256 {snapshot.TextSha256[..Math.Min(12, snapshot.TextSha256.Length)]}… · CC BY-SA, by {StackExchangeAuthors(snapshot)}",
        MultivocalPages.FullTextBasis => $"Full text kept {snapshot.AccessedUtc:yyyy-MM-dd HH:mm} UTC from {snapshot.FullTextSource ?? "an open-access copy"} · {snapshot.Characters:N0} characters · SHA-256 {snapshot.TextSha256[..Math.Min(12, snapshot.TextSha256.Length)]}…",
        _ => $"Abstract only: no open full text ({(snapshot.FullTextSource ?? "none").Replace("none (", "").TrimEnd(')')}), so quotes come from the abstract · SHA-256 {snapshot.TextSha256[..Math.Min(12, snapshot.TextSha256.Length)]}…",
    };

    /// <summary>The authors of a kept Stack Exchange thread, for its CC BY-SA attribution.</summary>
    public static string StackExchangeAuthors(PageSnapshot snapshot)
    {
        var names = (snapshot.Attribution ?? new()).Select(c => c.Author).Where(a => a.Length > 0).Distinct().ToList();
        return names.Count == 0 ? "its authors" : names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
    }

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

    /// <summary>A grey search's name, or an academic database's with "(academic)", since the two pools are listed together.</summary>
    public static string SearchLabel(string key) =>
        GreySourceCatalog.Create(key)?.Name
        ?? (SourceCatalog.Find(key) is { } database ? $"{database.DisplayName} (academic)" : MultivocalGuidelines.SearchLabel(key));

    /// <summary>What a finding's source is: its kind, outlet tier and points, or for an academic source its text basis (decision 8).</summary>
    public static string FindingLabel(AuBtechReviewAgent.SynthesisFinding finding) => finding.Literature == "formal"
        ? $"{KindLabel(finding.Kind)}, {finding.EvidenceBasis}"
        : $"{KindLabel(finding.Kind)}, {finding.Tier switch { 1 => "1st", 2 => "2nd", _ => "3rd" }} tier, {finding.QualityPoints.ToString("0.#")} points";

    public static string KindLabel(string key) =>
        key == MultivocalSearcher.FormalKind ? "Academic literature"
        : MultivocalGuidelines.GreyTypes.FirstOrDefault(t => t.Key == key).Label ?? key;
}
