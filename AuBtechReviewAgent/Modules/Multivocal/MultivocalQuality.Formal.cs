using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The appraisal of a formal source (block I-4): its open full text, or its abstract when there is none, is kept as
/// its text (decision 8), and it is appraised with the systematic review's own extraction and MMAT appraisal
/// (<see cref="StudyExtractor"/>), every answer backed by a quote found in that text. Table 7 is for grey literature,
/// so a formal source gets no points, and the quality threshold does not apply to it: an appraised source is kept.
/// </summary>
public sealed partial class MultivocalQualityAssessor
{
    /// <summary>Part of the cache key of a formal source's appraisal: raise it whenever that appraisal changes.</summary>
    public const string FormalAppraisalVersion = "formal-mmat-v1";

    private async Task<GreyQuality> AppraiseFormalAsync(Guid runId, string? editKey, IChatCompletionService chat, GreyRecord record, bool fetch = true)
    {
        var result = new GreyQuality { Address = MultivocalSearcher.AddressKey(record.Url), Title = record.Title, Url = record.Url, Formal = true };
        var paper = _searcher.LoadLedger(runId)?.FormalPool.FirstOrDefault(s => MultivocalSearcher.AddressKey(s.Record.Url) == result.Address)?.Paper;
        if (paper == null) return NotAssessed(result, "the database's record of this source is not in the ledger");

        var snapshot = _pages.Load(runId).Pages.FirstOrDefault(p => MultivocalSearcher.AddressKey(p.Url) == result.Address);
        if (snapshot is not { NotKept: null } && fetch) snapshot = await _pages.KeepAsync(runId, editKey, record.Url);
        if (snapshot == null) return NotAssessed(result, "the text was not kept");
        string? text = snapshot.NotKept == null ? _pages.ReadText(runId, snapshot) : null;
        if (text == null) return NotAssessed(result, snapshot.NotKept ?? "the kept text no longer matches its fingerprint");
        result.TextSha256 = snapshot.TextSha256;
        result.TextBasis = snapshot.TextBasis;

        string key = ReviewCache.Key(FormalAppraisalVersion, _model, paper.Id, paper.Title ?? "", snapshot.TextSha256);
        if (_cache.TryGet<StudyExtraction>("formal-appraisal", key, out var cached) && cached is { Error: null })
        {
            result.FromCache = true;
            result.Mmat = cached;
        }
        else
        {
            var appraisal = await StudyExtractor.ExtractAsync(chat, MultivocalPages.AsPaper(1, paper, snapshot, text));
            if (appraisal.Error != null)
            {
                _log.LogWarning("Formal appraisal failed for '{Title}': {Message}", record.Title.Replace('\r', ' ').Replace('\n', ' '), appraisal.Error.Replace('\r', ' ').Replace('\n', ' '));
                return NotAssessed(result, appraisal.Error.Replace('\r', ' ').Replace('\n', ' '));
            }
            _cache.Set("formal-appraisal", key, appraisal);
            result.Mmat = appraisal;
        }
        result.Outcome = "Passed";
        return result;
    }

    private static GreyQuality NotAssessed(GreyQuality result, string why)
    {
        result.Outcome = "Not assessed";
        result.NotAssessed = why;
        return result;
    }
}
