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

    private CanonicalText(string text, bool isBlank)
    {
        _text = text;
        IsBlank = isBlank;
    }

    /// <summary>Letters and digits only, lower case, after Unicode normalisation.</summary>
    public string Value => _value ??= IsBlank ? "" : CitationSupportChecker.Canonical(_text);

    /// <summary>True when the original text was missing or only white space: no quote is found in it.</summary>
    public bool IsBlank { get; }

    public static CanonicalText Of(string? text) => new(text ?? "", string.IsNullOrWhiteSpace(text));
}
