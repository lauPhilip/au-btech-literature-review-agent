using System;
using System.Linq;
using Xunit;
using Xunit.Sdk;

namespace AuBtechReviewAgent.Tests;

/// <summary>The fuzz harness itself (#623): a seed gives the same string every time, and the strings reach the cases the guards must survive.</summary>
public class FuzzHarnessTests
{
    [Fact]
    public void A_seed_gives_the_same_text_every_time()
    {
        foreach (int seed in new[] { 1, 42, 123456 })
        {
            Assert.Equal(Fuzz.Text(new Random(seed)), Fuzz.Text(new Random(seed)));
            Assert.Equal(Fuzz.Key(new Random(seed)), Fuzz.Key(new Random(seed)));
        }
    }

    [Fact]
    public void The_generated_text_reaches_the_awkward_cases()
    {
        var texts = Enumerable.Range(0, 3000).Select(seed => Fuzz.Text(new Random(seed))).ToList();
        Assert.Contains(texts, t => Fuzz.HasLoneSurrogate(t));
        Assert.Contains(texts, t => t.Contains('\u200B'));
        Assert.Contains(texts, t => t.Contains("\uDB40"));
        Assert.Contains(texts, t => t.Contains("<<<"));
        Assert.Contains(texts, t => t.Contains('\u0001'));
        Assert.Contains(texts, t => t.Any(c => c is >= '\u0600' and <= '\u06FF') && t.Any(c => c is >= 'a' and <= 'z'));
        Assert.Contains(texts, t => t.Length > 0 && t.All(c => c is >= ' ' and <= '~' or '\n' or '\u00E9'));
    }

    [Fact]
    public void A_failure_names_its_seed_and_prints_the_input_as_escapes()
    {
        var e = Assert.Throws<XunitException>(() => Fuzz.Run("demo", s => throw new InvalidOperationException("boom")));
        Assert.Matches(@"demo failed on seed -?\d+ \(replay with FUZZ_SEED=-?\d+\)", e.Message);
        Assert.Contains("Input: \"", e.Message);
        Assert.Contains("boom", e.Message);
        Assert.Equal("\"a\\u200B\\uD83D\"", Fuzz.Escape("a\u200B\uD83D"));
    }

    [Fact]
    public void A_property_runs_many_cases_within_its_budget()
    {
        if (Fuzz.FixedSeed != null) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int cases = Fuzz.Run("count", _ => { });
        Assert.True(cases > 100, $"only {cases} cases ran");
        Assert.True(clock.Elapsed < Fuzz.Budget + TimeSpan.FromSeconds(2));
    }
}
