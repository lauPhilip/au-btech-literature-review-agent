using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AuBtechReviewAgent;

/// <summary>One extracted fact with the sentence from the paper that supports it.</summary>
public class EvidencedValue
{
    public string Value { get; set; } = "";
    public string Quote { get; set; } = "";
    /// <summary>True when the quote was found word for word in the paper text the model was shown.</summary>
    public bool QuoteVerified { get; set; }

    /// <summary>
    /// For a key finding of a run with research questions: the question it bears on ("RQ1") or "none", checked in code
    /// against the run's question ids. Null (and not written) when the run had no research questions.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Question { get; set; }
}

/// <summary>One MMAT 2018 criterion and the model's answer to it.</summary>
public class AppraisalAnswer
{
    public string Id { get; set; } = "";
    public string Criterion { get; set; } = "";
    /// <summary>yes, no or cant_tell (MMAT's own answer options).</summary>
    public string Answer { get; set; } = "";
    public string Quote { get; set; } = "";
    public bool QuoteVerified { get; set; }
}

/// <summary>Data extraction and quality appraisal for one included paper (written to extraction.json).</summary>
public class StudyExtraction
{
    public int ReferenceNumber { get; set; }
    public string PaperId { get; set; } = "";
    public string Title { get; set; } = "";
    public string EvidenceBasis { get; set; } = "";
    public string StudyType { get; set; } = "";
    public EvidencedValue Method { get; set; } = new();
    public EvidencedValue Sample { get; set; } = new();
    public List<EvidencedValue> KeyFindings { get; set; } = new();
    public EvidencedValue Limitations { get; set; } = new();

    /// <summary>MMAT category: qualitative, quantitative_randomized, quantitative_nonrandomized, quantitative_descriptive, mixed_methods or not_empirical.</summary>
    public string AppraisalCategory { get; set; } = "";
    public List<AppraisalAnswer> Appraisal { get; set; } = new();
    public string? Error { get; set; }
    /// <summary>
    /// What the code did beyond the model's answer, e.g. that the answer was kept after a problem the model did not
    /// fix when it was sent back. Null when there is nothing to say.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>True when this extraction was reused from an earlier identical one (same prompt, prompt version and model).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FromCache { get; set; }

    [JsonIgnore] public int AppraisalYes => Appraisal.Count(a => a.Answer == "yes");
}
