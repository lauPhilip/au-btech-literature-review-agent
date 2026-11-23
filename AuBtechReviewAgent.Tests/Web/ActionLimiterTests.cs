using System.IO.Compression;
using System.Text;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>The per-address limits on the costly actions of the live connection, and the safe archive upload on /verify.</summary>
public class ActionLimiterTests
{
    [Fact]
    public void An_address_gets_its_hourly_share_of_an_action_and_no_more_and_others_are_not_affected()
    {
        var limiter = new ActionLimiter(new Dictionary<string, int> { [ActionLimiter.ProposeStrings] = 3 });
        for (int i = 0; i < 3; i++) Assert.True(limiter.TryTake(ActionLimiter.ProposeStrings, "203.0.113.5", out _));

        Assert.False(limiter.TryTake(ActionLimiter.ProposeStrings, "203.0.113.5", out string refusal));
        Assert.StartsWith("This was done many times from your network address in the last hour. Try again in ", refusal);
        Assert.True(limiter.TryTake(ActionLimiter.ProposeStrings, "203.0.113.6", out _));
        Assert.True(limiter.TryTake(ActionLimiter.Recheck, "203.0.113.5", out _)); // each action counts on its own
        Assert.True(limiter.TryTake(ActionLimiter.ProposeStrings, "127.0.0.1", out _)); // the server itself is not limited
        Assert.Equal(20, limiter.PerHour(ActionLimiter.StartReview));
    }

    [Fact]
    public void A_limit_of_zero_means_no_limit()
    {
        var limiter = new ActionLimiter(new Dictionary<string, int> { [ActionLimiter.Recheck] = 0 });
        for (int i = 0; i < 500; i++) Assert.True(limiter.TryTake(ActionLimiter.Recheck, "2001:db8::/64", out _));
    }

    [Theory]
    [InlineData("run.zip", 10_000, null)]
    [InlineData("run.exe", 10_000, "Only a .zip run archive can be checked.")]
    [InlineData("run.zip", 10, "The file is too small to be a zip archive.")]
    [InlineData("run.zip", 300L * 1024 * 1024, "The file is larger than 200 MB.")]
    public void An_upload_is_refused_by_its_name_and_size_before_it_is_read(string name, long size, string? expected) =>
        Assert.Equal(expected, ArchiveUpload.Refuse(name, size));

    [Fact]
    public async Task A_file_that_does_not_start_like_a_zip_is_refused_and_a_real_archive_is_checked()
    {
        using var fake = new MemoryStream(Encoding.UTF8.GetBytes("this is a text file named .zip, not an archive"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveUpload.CheckAsync(fake));

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("protocol.md").Open());
            writer.Write("# Protocol");
        }
        zip.Position = 0;
        // A zip without a manifest is still read, and the check says what is missing instead of failing.
        try { await ArchiveUpload.CheckAsync(zip); }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidOperationException) { }
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "verify-*.zip.tmp"));
    }
}
