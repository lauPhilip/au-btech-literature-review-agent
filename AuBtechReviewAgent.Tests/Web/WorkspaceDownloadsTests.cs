using System.Text;
using AuBtechReviewAgent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #306: the run downloads carry Last-Modified and an ETag from the run folder's stamp, and a repeat download
/// of an unchanged run is answered 304 without building anything.
/// </summary>
public class WorkspaceDownloadsTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "downloads-" + Guid.NewGuid().ToString("N"));
    private readonly RunStore _runs;
    private readonly Guid _run = Guid.NewGuid();
    private int _builds;

    public WorkspaceDownloadsTests()
    {
        _runs = new RunStore(_root);
        Directory.CreateDirectory(Path.Join(_runs.FolderOf(_run), RunArchive.RawResponsesFolder));
        File.WriteAllText(Path.Join(_runs.FolderOf(_run), RunStore.ProtocolFile), "# Protocol");
        File.WriteAllText(Path.Join(_runs.FolderOf(_run), RunArchive.RawResponsesFolder, "search-1.json"), "[]");
    }

    public void Dispose() => TestFolders.TryDelete(_root);

    private byte[]? Build()
    {
        _builds++;
        return File.Exists(Path.Join(_runs.FolderOf(_run), RunStore.ProtocolFile)) ? Encoding.UTF8.GetBytes("@article{a}") : null;
    }

    private async Task<HttpContext> Download(Action<IHeaderDictionary>? ask = null)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        ask?.Invoke(context.Request.Headers);
        var result = WorkspaceDownloads.Serve(_runs, context, _run, Build, "application/x-bibtex", "references.bib");
        await result.ExecuteAsync(context);
        return context;
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private void Touch(string name, string content) => File.WriteAllText(Path.Join(_runs.FolderOf(_run), name), content);

    [Fact]
    public async Task AFirstDownloadCarriesAnETagAndLastModified()
    {
        var context = await Download();

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("@article{a}", Body(context));
        Assert.Equal(RunFolderStamp.Of(_runs.FolderOf(_run))!.ETag, context.Response.Headers.ETag.ToString());
        Assert.False(string.IsNullOrEmpty(context.Response.Headers.LastModified.ToString()));
        Assert.Contains("references.bib", context.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task ARepeatDownloadWithTheETagIs304AndBuildsNothing()
    {
        string etag = (await Download()).Response.Headers.ETag.ToString();

        var again = await Download(h => h.IfNoneMatch = etag);

        Assert.Equal(304, again.Response.StatusCode);
        Assert.Equal(1, _builds);
        Assert.Equal(etag, again.Response.Headers.ETag.ToString());
        Assert.Equal("", Body(again));
    }

    [Fact]
    public async Task ARepeatDownloadWithTheDateIs304()
    {
        string lastModified = (await Download()).Response.Headers.LastModified.ToString();

        Assert.Equal(304, (await Download(h => h.IfModifiedSince = lastModified)).Response.StatusCode);
        Assert.Equal(200, (await Download(h => h.IfModifiedSince = "Mon, 01 Jan 2024 00:00:00 GMT")).Response.StatusCode);
    }

    [Fact]
    public async Task AChangeInTheRunFolderGivesTheFileAgain()
    {
        string etag = (await Download()).Response.Headers.ETag.ToString();
        Touch(RunStore.NotesFile, "[{\"Text\":\"A new note\"}]");

        var again = await Download(h => h.IfNoneMatch = etag);

        Assert.Equal(200, again.Response.StatusCode);
        Assert.NotEqual(etag, again.Response.Headers.ETag.ToString());
        Assert.Equal(2, _builds);
    }

    [Fact]
    public async Task AnOldETagOrAMissingFileIsNotA304()
    {
        Assert.Equal(200, (await Download(h => h.IfNoneMatch = "\"0123456789abcdef\"")).Response.StatusCode);
        File.Delete(Path.Join(_runs.FolderOf(_run), RunStore.ProtocolFile));
        Assert.Equal(404, (await Download()).Response.StatusCode);
    }

    [Fact]
    public async Task ARunThatDoesNotExistIs404()
    {
        TestFolders.TryDelete(_runs.FolderOf(_run));

        Assert.Equal(404, (await Download()).Response.StatusCode);
        Assert.Equal(0, _builds);
    }

    [Fact]
    public void TheStampFollowsEveryFileButNotHalfWrittenOnes()
    {
        string folder = _runs.FolderOf(_run);
        var first = RunFolderStamp.Of(folder)!;

        Touch("half-written.json.0a1b.tmp", "x");
        Touch(RunArchiveCache.Prefix + first.Key + RunArchiveCache.Suffix, "zip");
        Assert.Equal(first, RunFolderStamp.Of(folder));

        File.WriteAllText(Path.Join(folder, RunArchive.RawResponsesFolder, "search-2.json"), "[]");
        var second = RunFolderStamp.Of(folder)!;
        Assert.NotEqual(first.ETag, second.ETag);

        File.Delete(Path.Join(folder, RunArchive.RawResponsesFolder, "search-2.json"));
        Assert.Equal(first.ETag, RunFolderStamp.Of(folder)!.ETag);
        Assert.Null(RunFolderStamp.Of(Path.Join(_root, "missing")));
    }

    [Fact]
    public void TheLastModifiedDateHasWholeSeconds()
    {
        var stamp = RunFolderStamp.Of(_runs.FolderOf(_run))!;

        Assert.Equal(0, stamp.LastModified.Ticks % TimeSpan.TicksPerSecond);
        Assert.Matches("^\"[0-9a-f]{32}\"$", stamp.ETag);
    }

    [Fact]
    public async Task TheLedgerIsServedAsItIsOnDiskAndIsMissingForARunWithoutOne()
    {
        Assert.Null(WorkspaceDownloads.LedgerBytes(_runs, _run)); // a multivocal run keeps no transparent-process.json
        string ledger = "{\"Method\":\"systematic\",\"ScreeningLogs\":[{\"Decision\":\"Excluded\",\"Reasoning\":\"About marketing\"}]}";
        Touch(GallerySnapshot.LedgerFile, ledger);

        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await WorkspaceDownloads.Serve(_runs, context, _run, () => WorkspaceDownloads.LedgerBytes(_runs, _run),
            "application/json; charset=utf-8", WorkspaceDownloads.LedgerDownload).ExecuteAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(ledger, Body(context));
        Assert.Contains("ledger.json", context.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public void TheLedgerCanBeReadWhileTheRunHoldsItOpenForWriting()
    {
        Touch(GallerySnapshot.LedgerFile, "{}");
        using var writer = new FileStream(Path.Join(_runs.FolderOf(_run), GallerySnapshot.LedgerFile), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal("{}", Encoding.UTF8.GetString(WorkspaceDownloads.LedgerBytes(_runs, _run)!));
    }
}
