using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// The first half of the quality step: keeping every page that is not kept yet, with its own slots, before any model
/// call. The model slots then never wait on the network, and a page that could not be fetched is on file as the reason
/// its source was not assessed. Each site is still paced by <see cref="PageFetcher"/>, so more slots only mean more
/// sites at once, never more requests to one site. Stack Exchange questions are kept from the API first, in batches.
/// </summary>
public sealed partial class MultivocalQualityAssessor
{
    /// <summary>How many pages are fetched at once, across all sites.</summary>
    public const int DefaultFetchParallelism = 16;

    /// <summary>Keeps the pages of <paramref name="records"/> that are not kept; returns why a page could not be fetched, by address.</summary>
    private async Task<IReadOnlyDictionary<string, string>> KeepMissingAsync(Guid runId, string? editKey, IReadOnlyList<GreyRecord> records, IProgress<string>? progress)
    {
        var kept = _pages.Load(runId).Pages.Where(p => p.NotKept == null).Select(p => MultivocalSearcher.AddressKey(p.Url)).ToHashSet();
        var missing = records.Where(r => !kept.Contains(MultivocalSearcher.AddressKey(r.Url))).ToList();
        var failed = new ConcurrentDictionary<string, string>();
        int done = 0;

        // Stack Exchange questions come from the API, many to a request, never from the site's pages (#453).
        var questions = missing.Where(r => MultivocalPages.StackExchangeQuestion(r) != null).ToList();
        if (questions.Count > 0)
        {
            await _pages.KeepStackExchangeAsync(runId, editKey, questions);
            done = questions.Count;
            progress?.Report($"{done} of {missing.Count} pages kept");
        }
        using var slots = new SemaphoreSlim(_fetchParallelism);
        await Task.WhenAll(missing.Except(questions).Select(async record =>
        {
            await slots.WaitAsync();
            try
            {
                await _pages.KeepAsync(runId, editKey, record.Url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failed[MultivocalSearcher.AddressKey(record.Url)] = $"the page could not be fetched ({ex.Message.Replace('\r', ' ').Replace('\n', ' ')})";
            }
            finally
            {
                slots.Release();
                progress?.Report($"{Interlocked.Increment(ref done)} of {missing.Count} pages kept");
            }
        }));
        return failed;
    }

    /// <summary>A source whose page could not be fetched: not assessed, with the reason, and no model call.</summary>
    private static GreyQuality Unfetched(GreyRecord record, string why) => NotAssessed(new GreyQuality
    {
        Address = MultivocalSearcher.AddressKey(record.Url),
        Title = record.Title,
        Url = record.Url,
        Formal = record.Kind == MultivocalSearcher.FormalKind,
    }, why);
}
