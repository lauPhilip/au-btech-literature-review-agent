using System.Text;
using System.Text.RegularExpressions;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The quote check against a text made canonical once must answer exactly as the check that normalised the text for
/// every quote. <see cref="Reference"/> is the canonical match as it was before #606, kept here word for word: the
/// check may now refuse a match it found (one that is not a run of text), never find one it missed. Both are run on
/// every attack-set text and on generated quotes with ellipses, glue words, ligatures, hyphens, smart quotes, accents
/// and CJK text.
/// </summary>
public class CanonicalTextTests
{
    private static bool Reference(string? quote, string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(excerpt)) return false;
        string text = ReferenceCanonical(excerpt);
        var parts = Regex.Split(quote, @"\.{3}|…")
            .Select((p, i) => i == 0 ? p : Regex.Replace(p, @"^[\s,;:]*(and|or|but)\s+", "", RegexOptions.IgnoreCase))
            .Select(ReferenceCanonical).Where(p => p.Length > 0).ToList();
        if (parts.Count > 1) parts = parts.Where(p => p.Length >= 10).ToList();
        if (parts.Count == 0 || parts.Sum(p => p.Length) < 15) return false;
        int from = 0;
        foreach (var part in parts)
        {
            int at = text.IndexOf(part, from, StringComparison.Ordinal);
            if (at < 0) return false;
            from = at + part.Length;
        }
        return true;
    }

    private static string ReferenceCanonical(string s)
    {
        string text = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static readonly string[] Texts =
    {
        "Agentic AI goes beyond AI-assisted programming, allowing AI systems to autonomously decompose their work, make decisions based on context, reason together, use tools dynamically, and learn continuously.",
        "In practice, differences in termi- nology and back-\nground between sub-communities have resulted in a certain degree of frag­mentation, with occasional reinvention of existing ideas under neighboring ﬁelds and ﬂows.",
        "The team said: “We keep a running summary of the conversation.” It’s cheaper — and the café’s naïve résumé shows Ångström-level détail.",
        "大型语言模型的上下文工程需要仔细管理。上下文窗口有限，因此摘要和检索很重要。 Context windows are finite and summaries help.",
        "Results: 42% of respondents (n = 1,204) reported that retrieval and/or summarisation improved accuracy; but latency rose by 3.5×.",
        "   ",
        "",
        "--- ... ---",
    };

    private static IEnumerable<string> QuotesFrom(string text, Random random)
    {
        string[] glue = { " and ", " or ", " but ", ", and ", " AND ", " Or ", "" };
        string[] ellipsis = { "...", "…", " ... ", "...." };
        for (int i = 0; i < 40 && text.Length > 2; i++)
        {
            int a = random.Next(text.Length), b = random.Next(a, Math.Min(text.Length, a + 160) + 1);
            string q = text[a..b];
            yield return q;
            yield return q.ToUpperInvariant();
            yield return q.Replace("fi", "ﬁ").Replace("fl", "ﬂ").Replace('\'', '’').Replace('"', '“');
            yield return q.Replace(" ", "-\n");
            if (q.Length > 20)
            {
                int cut = random.Next(5, q.Length - 5), back = random.Next(cut, q.Length);
                yield return q[..cut] + ellipsis[random.Next(ellipsis.Length)] + glue[random.Next(glue.Length)] + q[back..];
                yield return q[back..] + "..." + q[..cut];
            }
        }
        yield return "";
        yield return "   ";
        yield return "...";
        yield return "too short";
    }

    public static IEnumerable<object[]> Cases()
    {
        var random = new Random(408);
        var texts = Texts.Concat(AttackSet.All.Select(c => c.Text)).ToList();
        foreach (var text in texts)
        {
            var quotes = QuotesFrom(text, random).Concat(QuotesFrom(texts[random.Next(texts.Count)], random)).ToList();
            yield return new object[] { text, quotes };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ACanonicalTextAnswersAsTheCheckThatNormalisedItEveryTime(string text, List<string> quotes)
    {
        var canonical = CanonicalText.Of(text);
        foreach (var quote in quotes)
        {
            // A slice can split a surrogate pair, which normalisation refuses: both must then refuse alike.
            string reference = Outcome(() => Reference(quote, text));
            string once = Outcome(() => CitationSupportChecker.QuoteOccursIn(quote, canonical));
            Assert.True(once == Outcome(() => CitationSupportChecker.QuoteOccursIn(quote, text)), $"string form, quote: {quote}");
            // The run-of-text check (#606) only takes matches away: it never finds what the canonical match missed.
            Assert.True(once == reference || (once == "not found" && reference == "found"), $"canonical form, quote: {quote}");
        }
    }

    private static string Outcome(Func<bool> check)
    {
        try { return check() ? "found" : "not found"; }
        catch (ArgumentException) { return "refused"; }
    }

    [Fact]
    public void TheGeneratedQuotesFindAndMissBoth()
    {
        var random = new Random(408);
        var answers = Texts.SelectMany(t => QuotesFrom(t, random).Select(q => Outcome(() => Reference(q, t)))).ToList();
        Assert.Contains("found", answers);
        Assert.Contains("not found", answers);
    }

    [Fact]
    public void ABlankTextFindsNothing()
    {
        Assert.True(CanonicalText.Of(null).IsBlank);
        Assert.True(CanonicalText.Of(" \n").IsBlank);
        Assert.False(CitationSupportChecker.QuoteOccursIn("a quote long enough to count here", CanonicalText.Of(null)));
    }
}
