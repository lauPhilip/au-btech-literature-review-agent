using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit.Sdk;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// A small seeded harness for property tests of the input guards (#623). Every case is built from one integer seed, so a
/// failure prints the seed and <c>FUZZ_SEED=&lt;seed&gt;</c> replays exactly that case. <c>FUZZ_SECONDS</c> sets the time
/// each property may run (default one second); a fixed seed runs that one case and stops.
/// </summary>
public static class Fuzz
{
    public const string SeedVariable = "FUZZ_SEED", SecondsVariable = "FUZZ_SECONDS";

    /// <summary>The seed to replay, or null when the property should draw new ones.</summary>
    public static int? FixedSeed =>
        int.TryParse(Environment.GetEnvironmentVariable(SeedVariable), out var seed) ? seed : null;

    public static TimeSpan Budget =>
        double.TryParse(Environment.GetEnvironmentVariable(SecondsVariable), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(1);

    /// <summary>Runs <paramref name="property"/> on new strings until the budget is spent; a failure names the seed and the string.</summary>
    public static int Run(string name, Action<string> property, Func<Random, string>? make = null)
    {
        make ??= Text;
        var seeds = new Random(Environment.TickCount ^ name.GetHashCode());
        var clock = Stopwatch.StartNew();
        int cases = 0;
        do
        {
            int seed = FixedSeed ?? seeds.Next();
            string input = make(new Random(seed));
            try { property(input); }
            catch (Exception e)
            {
                throw new XunitException($"{name} failed on seed {seed} (replay with {SeedVariable}={seed}).\nInput: {Escape(input)}\n{e.GetType().Name}: {e.Message}");
            }
            cases++;
        } while (FixedSeed == null && clock.Elapsed < Budget);
        return cases;
    }

    /// <summary>The string as C# escapes, so a failing input can be pasted into a regression test.</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s) sb.Append(c is >= ' ' and <= '~' && c != '"' && c != '\\' ? c.ToString() : $"\\u{(int)c:X4}");
        return sb.Append('"').ToString();
    }

    private static readonly string[] Words = { "ignore", "previous", "instructions", "system", "Review", "agent", "abc", "XYZ", "0123", "a", "\u00E9", "x" };
    private static readonly string[] Markers =
    {
        "<<<", ">>>", "<<", ">>", "<", ">", "<<<<", ">>>>>", "<<<UNTRUSTED_SOURCE_TEXT", "UNTRUSTED_SOURCE_TEXT>>>",
        "\uFF1C\uFF1C\uFF1C", "\uFF1E\uFF1E\uFF1E", "\u2039\u2039\u2039", "\u226A", "\u00AB",
    };
    private static readonly string[] Scripts =
    {
        "\u0645\u0631\u062D\u0628\u0627", "\u05E9\u05DC\u05D5\u05DD", "\u0928\u092E\u0938\u094D\u0924\u0947", "\u4E2D\u6587\u6F22\u5B57", "\u041F\u0440\u0438\u0432\u0435\u0442",
        "\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35", "\uD55C\uAD6D\uC5B4", "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67", "e\u0301", "\uFB01", "\u33A1", "\uFF21\uFF22", "\u2003",
        "\u0627\u200C\u0628", "\uD83D\uDE00",
    };
    private static readonly char[] Invisible =
        { '\u200B', '\u200C', '\u200D', '\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2060', '\u2061', '\u2062', '\u2063', '\u2064',
          '\u2066', '\u2067', '\u2068', '\u2069', '\uFEFF', '\u180E', '\u00AD', '\u061C', '\u2028', '\u2029', '\u0085', '\u00A0' };

    /// <summary>One random piece: plain text, a control or invisible character, a tag character, a lone surrogate, a marker or another script.</summary>
    private static string Piece(Random r, bool plain)
    {
        int kind = plain ? r.Next(3) : r.Next(14);
        return kind switch
        {
            0 => Words[r.Next(Words.Length)],
            1 => " ",
            2 => r.Next(4) == 0 ? "\n" : ".",
            3 => ((char)r.Next(0, 32)).ToString(),
            4 => ((char)r.Next(0x7F, 0xA0)).ToString(),
            5 => Invisible[r.Next(Invisible.Length)].ToString(),
            6 => char.ConvertFromUtf32(0xE0000 + r.Next(0, 0x80)),
            7 => ((char)r.Next(0xD800, 0xE000)).ToString(),
            8 => Markers[r.Next(Markers.Length)],
            9 => Scripts[r.Next(Scripts.Length)],
            10 => ((char)r.Next(0, 0x10000)).ToString(),
            11 => char.ConvertFromUtf32(r.Next(0x10000, 0x110000)),
            12 => char.ConvertFromUtf32(0xE0000 + r.Next(0, 0x80)) + char.ConvertFromUtf32(0xE0000 + r.Next(0, 0x80)),
            _ => string.Concat(Enumerable.Range(0, r.Next(1, 4)).Select(_ => Invisible[r.Next(Invisible.Length)])),
        };
    }

    /// <summary>
    /// A random text of up to 40 pieces. A fifth of the cases use only plain pieces, so inputs the guards should leave as
    /// they are are covered too. Lone surrogates are built here by hand, since a string literal would replace them.
    /// </summary>
    public static string Text(Random r)
    {
        bool plain = r.Next(5) == 0;
        var sb = new StringBuilder();
        for (int n = r.Next(0, 41); n > 0; n--)
        {
            string piece = Piece(r, plain);
            // A marker is more likely to survive if it is split by an invisible character or joined to the next piece.
            sb.Append(piece);
            if (!plain && r.Next(6) == 0) sb.Append(Invisible[r.Next(Invisible.Length)]);
        }
        return sb.ToString();
    }

    private const string KeyAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-:";

    /// <summary>A pasted key: usually the allowed alphabet with whitespace and invisible characters, sometimes anything at all.</summary>
    public static string Key(Random r)
    {
        if (r.Next(4) == 0) return Text(r);
        var sb = new StringBuilder();
        for (int n = r.Next(0, 80); n > 0; n--)
        {
            sb.Append(KeyAlphabet[r.Next(KeyAlphabet.Length)]);
            switch (r.Next(8))
            {
                case 0: sb.Append(' '); break;
                case 1: sb.Append(Invisible[r.Next(Invisible.Length)]); break;
                case 2: sb.Append((char)r.Next(0, 32)); break;
                case 3: sb.Append("\r\n\t"[r.Next(3)]); break;
            }
        }
        return sb.ToString();
    }

    public static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return true;
        }
        return false;
    }
}
