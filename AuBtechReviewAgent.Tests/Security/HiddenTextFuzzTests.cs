using System.Linq;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Random-text checks of <see cref="HiddenText.RemoveInvisible"/> (#623): what it promises in its summary, on text nobody wrote by hand.</summary>
public class HiddenTextFuzzTests
{
    private static bool IsTagPair(string s, int i) => s[i] == '\uDB40' && i + 1 < s.Length && s[i + 1] is >= '\uDC00' and <= '\uDC7F';

    private static int Count(string s, System.Func<char, bool> test) => s.Count(test);

    [Fact]
    public void RemoveInvisible_leaves_no_invisible_character_and_keeps_a_joiner_only_between_two_non_ASCII_characters()
    {
        Fuzz.Run("RemoveInvisible", text =>
        {
            string kept = HiddenText.RemoveInvisible(text).Text;
            for (int i = 0; i < kept.Length; i++)
            {
                char c = kept[i];
                Assert.False(c == '\u00AD' || IsTagPair(kept, i), $"U+{(int)c:X4} is left");
                if (c is '\u200C' or '\u200D')
                    Assert.True(i > 0 && i + 1 < kept.Length && kept[i - 1] > '\u007F' && kept[i + 1] > '\u007F', $"a joiner at {i} is not between two characters outside ASCII");
                else
                    Assert.False(HiddenText.IsInvisible(c), $"U+{(int)c:X4} is left");
            }
        });
    }

    [Fact]
    public void RemoveInvisible_counts_what_it_removes_and_keeps_everything_else_as_it_was()
    {
        Fuzz.Run("RemoveInvisible counts", text =>
        {
            var clean = HiddenText.RemoveInvisible(text);
            int tags = Enumerable.Range(0, text.Length).Count(i => IsTagPair(text, i));
            int invisible = Count(text, HiddenText.IsInvisible), soft = Count(text, c => c == '\u00AD');
            int joinersKept = Count(clean.Text, c => c is '\u200C' or '\u200D');
            int gone = text.Length - clean.Text.Length - soft;
            if (tags == 0 && !text.Contains('\uDB40'))
            {
                Assert.Equal(invisible - joinersKept, clean.InvisibleCharacters);
                Assert.Equal(gone, clean.InvisibleCharacters);
            }
            else
            {
                // Tag characters take two units and count once, and removing characters can join the halves of one.
                Assert.InRange(clean.InvisibleCharacters, (gone + 1) / 2, gone);
                Assert.True(clean.InvisibleCharacters >= tags);
            }
            Assert.All(clean.Hidden, c => Assert.InRange(c, ' ', '~'));
            if (tags + invisible + soft == 0) Assert.Equal(text, clean.Text);
        });
    }

    [Fact]
    public void RemoveInvisible_is_stable_when_run_again()
    {
        Fuzz.Run("RemoveInvisible twice", text =>
        {
            string once = HiddenText.RemoveInvisible(text).Text;
            var again = HiddenText.RemoveInvisible(once);
            Assert.Equal(once, again.Text);
            Assert.Equal(0, again.InvisibleCharacters);
        });
    }
}
