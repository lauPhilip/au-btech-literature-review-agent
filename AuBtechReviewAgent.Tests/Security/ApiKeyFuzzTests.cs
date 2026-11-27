using System.Linq;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Random-text checks of <see cref="SecurityUtility.SanitizeApiKey"/> (#623): it returns the allowed alphabet or nothing.</summary>
public class ApiKeyFuzzTests
{
    private static bool Allowed(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or ':';

    [Fact]
    public void SanitizeApiKey_returns_only_the_allowed_alphabet_or_nothing()
    {
        Fuzz.Run("SanitizeApiKey", key =>
        {
            string clean = SecurityUtility.SanitizeApiKey(key);
            Assert.All(clean, c => Assert.True(Allowed(c), $"U+{(int)c:X4} is returned"));
            Assert.True(clean.Length <= 512);
        }, Fuzz.Key);
    }

    [Fact]
    public void SanitizeApiKey_keeps_a_key_that_only_has_whitespace_and_invisible_characters_around_it()
    {
        Fuzz.Run("SanitizeApiKey noise", key =>
        {
            string expected = new(key.Where(Allowed).ToArray());
            bool noise = key.All(c => Allowed(c) || char.IsWhiteSpace(c) || char.IsControl(c) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
            if (!noise) return;
            Assert.Equal(expected.Length is > 0 and <= 512 ? expected : "", SecurityUtility.SanitizeApiKey(key));
        }, Fuzz.Key);
    }
}
