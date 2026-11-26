using AuBtechReviewAgent.Components.Paper;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The web addresses in a reference are links on the page and in a saved PDF (the source, its DOI and its archived
/// copy). Before, the references printed them as plain text, so a reader of the PDF could not follow them.
/// </summary>
public class LinkedTextTests
{
    [Fact]
    public void AddressesBecomeLinksAndTheirFullStopStaysText()
    {
        var parts = LinkedText.Parts("Ana (2025). Keeping context short. https://blog.example.org/a (accessed 2026-11-23; archived at https://web.archive.org/web/2026/https://blog.example.org/a).");

        Assert.Equal(new[] { "https://blog.example.org/a", "https://web.archive.org/web/2026/https://blog.example.org/a" }, parts.Where(p => p.Link).Select(p => p.Text));
        Assert.Equal(").", parts[^1].Text);
        Assert.Equal("Ana (2025). Keeping context short. https://blog.example.org/a (accessed 2026-11-23; archived at https://web.archive.org/web/2026/https://blog.example.org/a).",
            string.Concat(parts.Select(p => p.Text)));
    }

    [Fact]
    public void ADoiAtTheEndAndABracketThatBelongsToTheAddressAreKept()
    {
        Assert.Equal("https://doi.org/10.48550/arXiv.2607.02703", LinkedText.Parts("arXiv. https://doi.org/10.48550/arXiv.2607.02703").Single(p => p.Link).Text);
        Assert.Equal("https://en.wikipedia.org/wiki/Agent_(computing)", LinkedText.Parts("See https://en.wikipedia.org/wiki/Agent_(computing).").Single(p => p.Link).Text);
    }

    [Fact]
    public void OnlyWebAddressesAreLinked()
    {
        Assert.DoesNotContain(LinkedText.Parts("javascript:alert(1) ftp://example.org/x and plain text"), p => p.Link);
        Assert.Empty(LinkedText.Parts(null));
    }
}
