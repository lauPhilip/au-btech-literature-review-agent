using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>A topic with U+FFFE or a lone surrogate must not make the gallery fingerprint throw (#768).</summary>
public class GalleryFoldInvalidCodePointTests
{
    [Fact]
    public void Fold_IgnoresNoncharactersAndLoneSurrogates()
    {
        Assert.Equal(GalleryFingerprint.Fold("AI in schools"), GalleryFingerprint.Fold("AI in￾ sch\uD800ools"));
    }

    [Fact]
    public void Of_GivesTheSameFingerprintForCleanAndBrokenTopic()
    {
        string clean = GalleryFingerprint.Of("card", "AI in schools", new[] { "What works?" });
        string broken = GalleryFingerprint.Of("card", "AI in sch￾ools\uDC00", new[] { "What wo\uD800rks?" });
        Assert.Equal(clean, broken);
    }
}
