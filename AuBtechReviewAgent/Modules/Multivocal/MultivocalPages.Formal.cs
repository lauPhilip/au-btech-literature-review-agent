using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>
/// The kept text of a formal source (block I-4, decision 8): its open full text, found as the systematic review finds
/// it, or, when there is none, its abstract. Either is kept like a web page's text, with its SHA-256, so the quote
/// check and the citation check work as they do for a page, and its text basis is on file.
/// </summary>
public sealed partial class MultivocalPages
{
    /// <summary>The folder in the run with the downloaded PDFs; like the page texts, it is not in the archive.</summary>
    public const string FullTextFolder = "FullText";

    public const string FullTextBasis = "full text";
    public const string AbstractBasis = "abstract only";

    private const string ChunkBreak = "\n\n";

    private async Task<PageSnapshot> KeepFormalAsync(Guid runId, GreyRecord record, AcademicPaper paper)
    {
        var accessed = DateTime.UtcNow;
        FullTextResult fullText;
        try
        {
            fullText = _fullText == null
                ? new FullTextResult(Array.Empty<DocumentChunk>(), "none (no full-text search on this server)", null)
                : await _fullText(paper, Path.Join(_runs.FolderOf(runId), FullTextFolder));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            fullText = new FullTextResult(Array.Empty<DocumentChunk>(), $"none (download failed: {PrismaReviewEngine.SanitizeLogMessage(ex.Message)})", null);
        }

        string fullTextText = string.Join(ChunkBreak, fullText.Chunks.Select(c => c.Text.Trim()).Where(t => t.Length > 0));
        string abstractText = (paper.Abstract ?? "").Trim();
        bool full = fullTextText.Length > 0;
        string? text = full ? fullTextText : abstractText.Length > 0 ? abstractText : null;
        var snapshot = new PageSnapshot
        {
            Url = record.Url,
            FinalUrl = full && PrismaReviewEngine.IsWebAddress(fullText.Url) ? fullText.Url! : record.Url,
            AccessedUtc = accessed,
            ContentType = full ? "application/pdf" : "text/plain",
            Title = record.Title,
            TextBasis = text == null ? null : full ? FullTextBasis : AbstractBasis,
            FullTextSource = fullText.Source,
            NotKept = text == null ? "Neither an open full text nor an abstract is available, so there is no text to quote." : null,
        };
        return full // letters the PDF hides were already taken out of the chunks (#534)
            ? await SaveAsync(runId, MultivocalSearcher.AddressKey(record.Url), snapshot, text, HiddenTextRemoved.Of(0, fullText.HiddenParts), fullText.Hidden)
            : await SaveAsync(runId, MultivocalSearcher.AddressKey(record.Url), snapshot, text);
    }

    /// <summary>
    /// A formal source's kept text as the systematic review's paper (<see cref="ReferencedPaper"/>): a full text in
    /// passages for the appraisal to choose from, or no passages and the abstract alone.
    /// </summary>
    public static ReferencedPaper AsPaper(int number, AcademicPaper paper, PageSnapshot snapshot, string text) =>
        snapshot.TextBasis == FullTextBasis
            ? new ReferencedPaper(number, paper.Id, paper.Title ?? "", paper.Abstract ?? "",
                text.Split(ChunkBreak, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select((t, i) => new DocumentChunk(paper.Id, i + 1, t)).ToList())
            : new ReferencedPaper(number, paper.Id, paper.Title ?? "", text, new List<DocumentChunk>());
}
