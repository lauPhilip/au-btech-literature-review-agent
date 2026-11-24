using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// Makes escaped text safe for pdflatex with the paper's preamble (<c>inputenc</c> utf8, the default OT1 font
/// encoding), which stops at any character it has no definition for, such as a Greek letter in a tool's name (issue #475).
/// Characters the preamble sets are kept as they are; Greek letters and common symbols become LaTeX; letters with an
/// accent OT1 lacks keep their base letter; spaces of other widths become a space; emoji and other symbols are
/// dropped, and any other character becomes "?". The safe set was found by compiling every character of the Latin,
/// punctuation, arrow and maths blocks with the paper's preamble.
/// </summary>
public static class LatexText
{
    // Characters beyond ASCII that the paper's preamble sets, besides the no-break space.
    private const string SetByPreamble =
        "¡¢£¤¥¦§¨©ª¬®¯°±²³´µ¶·¸¹º¼½¾¿ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÑÒÓÔÕÖ×ØÙÚÛÜÝßàáâãäåæçèéêëìíîïñòóôõö÷øùúûüýÿĀāĂăĆćĈĉĊċČč" +
        "ĎďĒēĔĕĖėĚěĜĝĞğĠġĢģĤĥĨĩĪīĬĭİıĲĳĴĵĶķĹĺĻļĽľŁłŃńŅņŇňŌōŎŏŐőŒœŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŨũŪūŬŭŮůŰűŴŵŶŷŸŹźŻżŽžƒǄǅǆǇ" +
        "ǈǉǊǋǌǍǎǏǐǑǒǓǔǢǣǦǧǨǩǰǴǵȘșȚțȲȳȷˆˇ˘˙˜˝‐‑‒–—―‖‘’“”†‡•…‰‱※‽⁄⁎⁒₡₤₦₩₫€₱℃№℗℞℠™Ω℧℮←↑→↓〈〉";

    private static readonly HashSet<int> Safe = new((" " + SetByPreamble).Select(c => (int)c));

    private static readonly Dictionary<int, string> Mapped = new()
    {
        // Greek, as maths letters, so no extra package is needed; capitals with a Latin shape are Latin letters.
        ['α'] = @"$\alpha$", ['β'] = @"$\beta$", ['γ'] = @"$\gamma$", ['δ'] = @"$\delta$", ['ε'] = @"$\varepsilon$",
        ['ζ'] = @"$\zeta$", ['η'] = @"$\eta$", ['θ'] = @"$\theta$", ['ι'] = @"$\iota$", ['κ'] = @"$\kappa$",
        ['λ'] = @"$\lambda$", ['μ'] = @"$\mu$", ['ν'] = @"$\nu$", ['ξ'] = @"$\xi$", ['ο'] = "o", ['π'] = @"$\pi$",
        ['ρ'] = @"$\rho$", ['ς'] = @"$\varsigma$", ['σ'] = @"$\sigma$", ['τ'] = @"$\tau$", ['υ'] = @"$\upsilon$",
        ['φ'] = @"$\phi$", ['χ'] = @"$\chi$", ['ψ'] = @"$\psi$", ['ω'] = @"$\omega$", ['ϵ'] = @"$\epsilon$",
        ['ϑ'] = @"$\vartheta$", ['ϕ'] = @"$\phi$",
        ['Γ'] = @"$\Gamma$", ['Δ'] = @"$\Delta$", ['Θ'] = @"$\Theta$", ['Λ'] = @"$\Lambda$", ['Ξ'] = @"$\Xi$",
        ['Π'] = @"$\Pi$", ['Σ'] = @"$\Sigma$", ['Υ'] = @"$\Upsilon$", ['Φ'] = @"$\Phi$", ['Ψ'] = @"$\Psi$", ['Ω'] = @"$\Omega$",
        ['Α'] = "A", ['Β'] = "B", ['Ε'] = "E", ['Ζ'] = "Z", ['Η'] = "H", ['Ι'] = "I", ['Κ'] = "K", ['Μ'] = "M",
        ['Ν'] = "N", ['Ο'] = "O", ['Ρ'] = "P", ['Τ'] = "T", ['Χ'] = "X",
        // Maths and arrows; ≥ ≤ → × were mapped before #475 and keep their old form.
        ['≥'] = @"$\geq$", ['≤'] = @"$\leq$", ['→'] = @"$\rightarrow$", ['×'] = @"$\times$", ['≈'] = @"$\approx$",
        ['≠'] = @"$\neq$", ['∞'] = @"$\infty$", ['−'] = "$-$", ['∼'] = @"$\sim$", ['∈'] = @"$\in$", ['∑'] = @"$\sum$",
        ['∏'] = @"$\prod$", ['√'] = @"$\surd$", ['∂'] = @"$\partial$", ['∇'] = @"$\nabla$", ['∀'] = @"$\forall$",
        ['∃'] = @"$\exists$", ['∧'] = @"$\wedge$", ['∨'] = @"$\vee$", ['⊂'] = @"$\subset$", ['⊆'] = @"$\subseteq$",
        ['∪'] = @"$\cup$", ['∩'] = @"$\cap$", ['≡'] = @"$\equiv$", ['∝'] = @"$\propto$", ['⋅'] = @"$\cdot$",
        ['∗'] = "*", ['↔'] = @"$\leftrightarrow$", ['⇒'] = @"$\Rightarrow$", ['⇐'] = @"$\Leftarrow$",
        ['⇔'] = @"$\Leftrightarrow$", ['✓'] = @"$\checkmark$", ['✔'] = @"$\checkmark$", ['✗'] = @"$\times$",
        ['✘'] = @"$\times$", ['★'] = @"$\star$", ['′'] = "'", ['″'] = "''",
        ['⁰'] = "$^{0}$", ['⁴'] = "$^{4}$", ['⁵'] = "$^{5}$", ['⁶'] = "$^{6}$", ['⁷'] = "$^{7}$", ['⁸'] = "$^{8}$",
        ['⁹'] = "$^{9}$", ['₀'] = "$_{0}$", ['₁'] = "$_{1}$", ['₂'] = "$_{2}$", ['₃'] = "$_{3}$", ['₄'] = "$_{4}$",
        // Letters and quotation marks OT1 has no glyph for.
        ['Ð'] = "D", ['ð'] = "d", ['Þ'] = "Th", ['þ'] = "th", ['Đ'] = "D", ['đ'] = "d", ['Ŋ'] = "N", ['ŋ'] = "n",
        ['«'] = "``", ['»'] = "''", ['‹'] = "`", ['›'] = "'", ['‚'] = ",", ['„'] = "``", ['˛'] = "",
        // Zero-width characters and the soft hyphen print nothing.
        [0x200B] = "", [0x200C] = "", [0x200D] = "", [0x2060] = "", [0xFEFF] = "", [0x00AD] = "",
    };

    /// <summary>Whether pdflatex with the paper's preamble sets this character from UTF-8.</summary>
    public static bool IsSafe(Rune r) => r.Value is '\n' or '\r' or '\t' || r.Value is >= 0x20 and < 0x7F || Safe.Contains(r.Value);

    /// <summary>The text with every character the preamble cannot set replaced, as the class summary says.</summary>
    public static string Map(string text)
    {
        if (text.All(c => c < 0x7F)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
        {
            if (Mapped.TryGetValue(r.Value, out var latex)) sb.Append(latex);
            else if (IsSafe(r)) sb.Append(r.ToString());
            else sb.Append(Fallback(r));
        }
        return sb.ToString();
    }

    private static string Fallback(Rune r)
    {
        if (Rune.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator) return " ";
        // A letter with an accent OT1 lacks ("ą", "ǫ", "ά"): its base letter, mapped like any other.
        string decomposed = r.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.EnumerateRunes().FirstOrDefault() is var b && b.Value != r.Value && b.Value != 0)
        {
            if (IsSafe(b)) return b.ToString();
            if (Mapped.TryGetValue(b.Value, out var latex)) return latex;
        }
        return Rune.GetUnicodeCategory(r) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control
            ? ""
            : "?";
    }
}
