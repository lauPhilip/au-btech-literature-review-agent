using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// A single inline citation marker that was removed because its number does not
/// point at a real entry in the run's reference list. Recorded for the audit trail.
/// </summary>
public record StrippedCitation(string Field, string InvalidMarker, int ReferenceListSize, string Action);

/// <summary>
/// The outcome of validating one block of text: the text with invalid markers removed,
/// and the list of markers that were stripped.
/// </summary>
public record CitationValidationResult(string CleanedText, IReadOnlyList<StrippedCitation> Stripped);

/// <summary>
/// Traceability guardrail for generated report text. Language models occasionally emit
/// inline citation markers such as [56] or [42, 75] that do not correspond to any entry
/// in the run's reference list. This class removes those out-of-range markers and reports
/// exactly which ones were removed, so a report never ships a citation that cannot be
/// traced back to a real source. Pure and side-effect free so it can be unit-tested on its own.
/// </summary>
public static class CitationValidator
{
    internal const string OutOfRangeAction = "Stripped - out of range of the run's reference list";

    // Matches a single "[n]" or a grouped "[n, m, ...]" marker.
    private static readonly Regex CitationMarker = new(@"\[(\d+(?:\s*,\s*\d+)*)\]", RegexOptions.Compiled);

    /// <summary>
    /// Removes every inline citation number that falls outside 1..<paramref name="referenceCount"/>
    /// from <paramref name="text"/>. Valid numbers are kept; a marker whose numbers are all invalid
    /// is removed entirely. When <paramref name="referenceCount"/> is zero or negative, or the text is
    /// empty, the text is returned unchanged and nothing is reported.
    /// </summary>
    /// <param name="text">The report text to check.</param>
    /// <param name="referenceCount">The number of entries in the run's reference list.</param>
    /// <param name="fieldName">A label for the field being checked, recorded against each removal.</param>
    public static CitationValidationResult ValidateAndStrip(string? text, int referenceCount, string fieldName)
    {
        var stripped = new List<StrippedCitation>();

        if (string.IsNullOrWhiteSpace(text) || referenceCount <= 0)
        {
            return new CitationValidationResult(text ?? string.Empty, stripped);
        }

        string cleaned = CitationMarker.Replace(text, match =>
        {
            var numbers = match.Groups[1].Value.Split(',').Select(n => n.Trim());
            var validNumbers = new List<string>();

            foreach (var numStr in numbers)
            {
                if (int.TryParse(numStr, out int num) && num >= 1 && num <= referenceCount)
                {
                    validNumbers.Add(numStr);
                }
                else
                {
                    stripped.Add(new StrippedCitation(fieldName, numStr, referenceCount, OutOfRangeAction));
                }
            }

            return validNumbers.Count > 0 ? $"[{string.Join(", ", validNumbers)}]" : "";
        });

        return new CitationValidationResult(cleaned, stripped);
    }
}
