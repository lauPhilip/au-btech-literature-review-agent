using System;
using System.Globalization;
using System.Text;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Random-text checks of what the guards promise (#623). The crafted tests elsewhere cover phrasings someone thought of;
/// these draw Unicode at random (lone surrogates, zero-width, bidirectional and tag characters, marker look-alikes, mixed
/// scripts) for a fixed short time and check the promises. A failure prints its seed; see FUZZ_SEED in docs/wiki/07.
/// </summary>
public class InputGuardFuzzTests
{
    private static bool IsAscii(char c) => c <= '\u007F';

    private static void NoJoinerBesideAscii(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\u200C' or '\u200D')) continue;
            Assert.True(i > 0 && i + 1 < text.Length && !IsAscii(text[i - 1]) && !IsAscii(text[i + 1]), $"a joiner at {i} is not between two characters outside ASCII");
        }
    }

    [Fact]
    public void Clean_never_throws_and_leaves_no_control_format_or_tag_character()
    {
        int cases = Fuzz.Run("Clean", text =>
        {
            foreach (bool singleLine in new[] { true, false })
            {
                string clean = ReviewInputGuard.Clean(text, singleLine).Text;
                foreach (var rune in clean.EnumerateRunes())
                {
                    var category = Rune.GetUnicodeCategory(rune);
                    if (category == UnicodeCategory.Control) Assert.True(!singleLine && rune.Value is '\n' or '\t', $"control character U+{rune.Value:X4} is left");
                    if (category == UnicodeCategory.Format) Assert.True(rune.Value is 0x200C or 0x200D, $"format character U+{rune.Value:X4} is left");
                }
                Assert.False(Fuzz.HasLoneSurrogate(clean), "a lone surrogate is left");
                Assert.True(clean.IsNormalized(NormalizationForm.FormKC), "the text is not in Unicode form KC");
                Assert.DoesNotContain(clean.EnumerateRunes(), r => r.Value is >= 0xE0000 and <= 0xE007F);
                NoJoinerBesideAscii(clean);
            }
        });
        Assert.True(cases > 0);
    }

    [Fact]
    public void Clean_defuses_every_marker_look_alike_and_tidies_the_whitespace()
    {
        Fuzz.Run("Clean markers", text =>
        {
            foreach (bool singleLine in new[] { true, false })
            {
                string clean = ReviewInputGuard.Clean(text, singleLine).Text;
                Assert.DoesNotContain("<<<", clean, StringComparison.Ordinal);
                Assert.DoesNotContain(">>>", clean, StringComparison.Ordinal);
                Assert.Equal(clean.Trim(), clean);
                Assert.DoesNotContain("  ", clean, StringComparison.Ordinal);
                Assert.DoesNotContain("\n\n\n", clean, StringComparison.Ordinal);
                if (singleLine) Assert.DoesNotContain('\n', clean);
            }
        });
    }

    [Fact]
    public void Clean_is_stable_when_run_again()
    {
        Fuzz.Run("Clean twice", text =>
        {
            string once = ReviewInputGuard.Clean(text, singleLine: false).Text;
            Assert.Equal(once, ReviewInputGuard.Clean(once, singleLine: false).Text);
        });
    }

    [Fact]
    public void Wrap_keeps_the_markers_for_itself()
    {
        Fuzz.Run("Wrap", text =>
        {
            string wrapped = PromptSafety.Wrap(text, "paper");
            Assert.StartsWith(PromptSafety.OpenTag, wrapped, StringComparison.Ordinal);
            Assert.EndsWith(PromptSafety.CloseTag, wrapped, StringComparison.Ordinal);
            Assert.Equal(1, wrapped.Split(PromptSafety.OpenTag).Length - 1);
            Assert.Equal(1, wrapped.Split(PromptSafety.CloseTag).Length - 1);
            Assert.Equal(1, wrapped.Split("<<<").Length - 1);
            Assert.Equal(1, wrapped.Split(">>>").Length - 1);
        });
    }
}
