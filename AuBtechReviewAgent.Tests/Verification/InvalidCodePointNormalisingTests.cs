using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Paper text with U+FFFE or a lone surrogate must not make the quote check throw (#768): both are dropped before the
/// text is normalised, so the result is the same as for the clean text.
/// </summary>
public class InvalidCodePointNormalisingTests
{
    private const string Clean = "Teachers reported higher engagement";
    private const string Broken = "Teachers re￾ported high\uD800er engage\uDC00ment";

    [Fact]
    public void Canonical_IgnoresNoncharactersAndLoneSurrogates()
    {
        string result = CitationSupportChecker.Canonical(Broken);
        Assert.Equal(CitationSupportChecker.Canonical(Clean), result);
    }

    [Fact]
    public void CanonicalText_ValueAndMapSurviveInvalidCodePoints()
    {
        var broken = CanonicalText.Of(Broken);
        var clean = CanonicalText.Of(Clean);
        Assert.Equal(clean.Value, broken.Value);
        Assert.NotNull(broken.Map);
        Assert.Equal(clean.Value.Length, broken.Map!.Value.Start.Length);
    }
}
