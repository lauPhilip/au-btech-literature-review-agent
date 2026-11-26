using System.Globalization;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// A source text in the form the quote check compares (<see cref="CitationSupportChecker.Canonical"/>), made once so
/// that every quote checked against the same text reuses it. Normalising a long page costs milliseconds; finding a
/// quote in the normalised text costs microseconds. The form is made on the first non-blank quote, as before, so a
/// text that cannot be normalised fails at the same point it always did.
/// </summary>
public sealed class CanonicalText
{
    private readonly string _text;
    private string? _value;
    private bool _mapped;
    private (int[] Start, int[] End)? _map;

    private CanonicalText(string text, bool isBlank)
    {
        _text = text;
        IsBlank = isBlank;
    }

    /// <summary>Letters and digits only, lower case, after Unicode normalisation.</summary>
    public string Value => _value ??= IsBlank ? "" : CitationSupportChecker.Canonical(_text);

    /// <summary>The text as given, which a canonical match is checked against (#606).</summary>
    public string Original => _text;

    /// <summary>True when the original text was missing or only white space: no quote is found in it.</summary>
    public bool IsBlank { get; }

    /// <summary>
    /// For each character of <see cref="Value"/>, the stretch of <see cref="Original"/> it came from (start inclusive,
    /// end exclusive), so a match in the canonical form can be read again in the text a reader sees. Made on first use,
    /// by normalising the text one character (grapheme) at a time. Null in the rare case where that does not give the
    /// same form as normalising the text whole; the quote check then keeps the canonical match alone.
    /// </summary>
    public (int[] Start, int[] End)? Map => _mapped ? _map : BuildMap();

    private (int[] Start, int[] End)? BuildMap()
    {
        string value = Value;
        _mapped = true;
        var start = new int[value.Length];
        var end = new int[value.Length];
        int n = 0;
        var elements = StringInfo.GetTextElementEnumerator(_text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int at = elements.ElementIndex;
            string form = element.Length == 1 && element[0] < 0x80
                ? element.ToLowerInvariant()
                : element.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            foreach (char c in form)
            {
                if (!char.IsLetterOrDigit(c)) continue;
                if (n >= value.Length || value[n] != c) return _map = null;
                start[n] = at;
                end[n] = at + element.Length;
                n++;
            }
        }
        return _map = n == value.Length ? (start, end) : null;
    }

    public static CanonicalText Of(string? text) => new(text ?? "", string.IsNullOrWhiteSpace(text));
}
