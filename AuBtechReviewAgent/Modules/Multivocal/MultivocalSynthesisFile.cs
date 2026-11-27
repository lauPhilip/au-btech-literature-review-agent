namespace AuBtechReviewAgent;

/// <summary>
/// One finding: an extracted value of one source, with the quote from its kept page. Its labels (kind, outlet tier,
/// quality points, grey or formal) are decided in code, so the synthesis can say what kind of evidence a theme rests on.
/// </summary>
public sealed class SynthesisFinding
{
    /// <summary>"F3.2": the second finding of the third source.</summary>
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";

    /// <summary>"grey" or "formal".</summary>
    public string Literature { get; set; } = "grey";

    /// <summary>1, 2 or 3: the outlet tier of Table 7 (1st tier scores 1 point on item 8.1).</summary>
    public int Tier { get; set; }
    public double QualityPoints { get; set; }

    /// <summary>What the quote was checked against: always the kept page text, never the live page.</summary>
    public string EvidenceBasis { get; set; } = "page text";
}

/// <summary>Two findings from different sources that disagree, as the model read them.</summary>
public sealed class SynthesisTension
{
    public string FindingA { get; set; } = "";
    public string FindingB { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// Where the tension lies when the question has formal findings, decided in code from the two findings' pools:
    /// <see cref="ArtAgainstPractice"/>, "within the state of the art" or "within the state of the practice". Null,
    /// and left out of the file, in a grey literature review.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Between { get; set; }

    public const string ArtAgainstPractice = "the state of the art against the state of the practice";

    /// <summary>
    /// True when the tension rests on two findings that were checked to be example quotes of their values from two
    /// sources (issue #463). An uncited tension is the model's reading of two values: no citation and no pool label.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCited => FindingA.Length > 0 && FindingB.Length > 0;

    /// <summary>What the paper and the report say after an uncited tension.</summary>
    public const string UncitedNote = "No two quotes were found that state it, so no source is cited.";
}

/// <summary>One theme of one research question, with the evidence it rests on counted in code.</summary>
public sealed class SynthesisTheme
{
    public string Question { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Findings { get; set; } = new();
    public List<SynthesisTension> Tensions { get; set; } = new();

    // Decided in code from the findings:
    public int Sources { get; set; }
    public int GreySources { get; set; }
    public int FormalSources { get; set; }
    public int FirstTier { get; set; }
    public int SecondTier { get; set; }
    public int ThirdTier { get; set; }
    public double MeanQuality { get; set; }

    /// <summary>"grey and formal literature", "formal literature only", "grey literature only" or "3rd-tier grey literature only".</summary>
    public string EvidenceLabel { get; set; } = "";
}

public sealed class UnassignedFinding
{
    public string Finding { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>The synthesis of a multivocal run (multivocal-synthesis.json), which is in the run archive.</summary>
public sealed class MultivocalSynthesisFile
{
    public Guid RunId { get; set; }
    public DateTime SynthesisedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public List<SynthesisFinding> Findings { get; set; } = new();
    public List<SynthesisTheme> Themes { get; set; } = new();
    public List<UnassignedFinding> Unassigned { get; set; } = new();

    /// <summary>What every quote was checked against: the kept page text, never the live page.</summary>
    public string EvidenceBasis { get; set; } = "page text";

    /// <summary>Research questions with no finding at all, so the review cannot answer them from these sources.</summary>
    public List<string> Unanswered { get; set; } = new();

    /// <summary>A note per question, such as that no formal source has been searched yet.</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>
    /// Findings whose value is the map's "Other": the map had no value for them. They are listed in code, not themed by
    /// the model, as a sign that the map may need a new value (G12: the map is refined iteratively).
    /// </summary>
    public List<NotCodedValue> NotCoded { get; set; } = new();

    /// <summary>
    /// Values whose quote is on the page but does not say what the value claims, with the check's reason; they are
    /// left out of the findings (the support check, as the systematic review checks a citation).
    /// </summary>
    public List<UnsupportedValue> Unsupported { get; set; } = new();

    /// <summary>Per research question, how well a second, independent placement of the values agreed with the first.</summary>
    public List<PlacementAgreement> SecondPlacements { get; set; } = new();
}

/// <summary>The agreement of the second, independent placement of one question's values with the first.</summary>
public sealed class PlacementAgreement
{
    public string Question { get; set; } = "";
    public double? Kappa { get; set; }
    public int CellsCompared { get; set; }
    public int Agreements { get; set; }
    public List<string> Disagreements { get; set; } = new();

    /// <summary>Why (part of) the second placement could not be done; null when it was.</summary>
    public string? Error { get; set; }
}

/// <summary>An extracted value the support check left out, with its quote and why.</summary>
public sealed class UnsupportedValue
{
    public string Finding { get; set; } = "";
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>The findings of one question with the map's "Other" value for one attribute.</summary>
public sealed class NotCodedValue
{
    public string Question { get; set; } = "";
    public string Attribute { get; set; } = "";
    public List<string> Findings { get; set; } = new();
    public int Sources { get; set; }
}
