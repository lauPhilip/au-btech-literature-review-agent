using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A newer run of the same review replaces its gallery entry atomically: the old report is moved aside before the new
/// one is moved in, never deleted first, so a failed replace leaves the published report in place.
/// </summary>
public class GalleryReplaceTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "gallery-swap-" + Guid.NewGuid().ToString("N"));

    public GalleryReplaceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFolders.TryDelete(_root);

    private string Folder(string name, string text)
    {
        string path = Path.Join(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Join(path, "entry.json"), text);
        return path;
    }

    [Fact]
    public void TheNewReportTakesThePlaceOfTheOldAndNothingIsLeftOver()
    {
        string target = Folder("abc", "old");
        string temp = Folder(".new-abc", "new");

        GalleryStore.Swap(temp, target);

        Assert.Equal("new", File.ReadAllText(Path.Join(target, "entry.json")));
        Assert.Equal(new[] { "abc" }, Directory.GetDirectories(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void AFirstEntryIsMovedIn()
    {
        string temp = Folder(".new-abc", "new");

        GalleryStore.Swap(temp, Path.Join(_root, "abc"));

        Assert.Equal("new", File.ReadAllText(Path.Join(_root, "abc", "entry.json")));
    }

    [Fact]
    public void WhenTheNewReportCannotBeMovedInTheOldOneStays()
    {
        string target = Folder("abc", "old");
        string temp = Folder(".new-abc", "new");
        bool oldWasThereUntilTheFailedMove = false;
        void Move(string from, string to)
        {
            if (from == temp)
            {
                oldWasThereUntilTheFailedMove = Directory.GetDirectories(_root).Any(d => Path.GetFileName(d).StartsWith("abc.old-", StringComparison.Ordinal));
                throw new IOException("disk full");
            }
            Directory.Move(from, to);
        }

        Assert.Throws<IOException>(() => GalleryStore.Swap(temp, target, Move));

        Assert.True(oldWasThereUntilTheFailedMove, "the old report was moved aside, not deleted");
        Assert.Equal("old", File.ReadAllText(Path.Join(target, "entry.json")));
        Assert.DoesNotContain(Directory.GetDirectories(_root), d => Path.GetFileName(d).Contains(".old-", StringComparison.Ordinal));
    }
}
