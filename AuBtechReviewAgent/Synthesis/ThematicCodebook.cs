using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Synthesis" section of appsettings.json.</summary>
public class SynthesisOptions
{
    /// <summary>Code the studies and write one subsection per theme. When false (or when coding fails), the
    /// synthesis is written in one pass as before.</summary>
    public bool ThematicSynthesis { get; set; } = true;

    /// <summary>A second, independent assignment of studies to themes, compared with Cohen's kappa.</summary>
    public bool DualCoding { get; set; } = true;

    /// <summary>Rewrite cited sentences the support check judged not supported or partly supported, then check them again.</summary>
    public bool RepairCitations { get; set; } = true;

    /// <summary>Give partly supported and not supported citations a second, independent check with more excerpts.</summary>
    public bool SecondCitationCheck { get; set; } = true;

    public int MaxThemes { get; set; } = 8;

    /// <summary>Studies per coding call.</summary>
    public int CodingBatchSize { get; set; } = 6;
}

/// <summary>One code: a short label for what a study found, anchored to verified evidence.</summary>
public class ThematicCode
{
    public string Id { get; set; } = "";
    public int Reference { get; set; }
    public string Label { get; set; } = "";

    /// <summary>Ids of the verified extraction findings the code comes from (e.g. "F3.1").</summary>
    public List<string> FindingIds { get; set; } = new();

    /// <summary>For a study without verified findings: a verbatim quote from its text.</summary>
    public string Quote { get; set; } = "";

    /// <summary>"verified finding", "verified quote" or "unanchored" (the evidence could not be confirmed in code).</summary>
    public string Anchor { get; set; } = "";

    /// <summary>The research questions the code bears on ("RQ1"), checked in code; null (and not written) for a run without research questions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Questions { get; set; }

    /// <summary>True when the coding answer was reused from an earlier identical one and checked again in code; null (and not written) otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FromCache { get; set; }
}

/// <summary>How many coding answers, and whether the codebook, were reused from earlier identical ones (written only when the cache is on).</summary>
public class SynthesisReuse
{
    public int CodingAnswers { get; set; }
    public int CodingAnswersReused { get; set; }
    public bool CodebookReused { get; set; }
}

public class Theme
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> CodeIds { get; set; } = new();

    /// <summary>Reference numbers of the studies with at least one code in this theme (derived in code).</summary>
    public List<int> Studies { get; set; } = new();

    /// <summary>The research questions the theme answers ("RQ1"); null when the run has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Questions { get; set; }

    /// <summary>Why the theme rests on one study, as the codebook gave it (issue #447); null for a theme of two or more studies.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SingleStudyReason { get; set; }
}

public record UncodedStudy(int Reference, string Reason);

/// <summary>A research question no code bears on ("RQ2"), with the codebook's reason; it is answered in code.</summary>
public record UnansweredQuestion(string Question, string Reason);

/// <summary>Coverage of one theme subsection: which of its studies are cited, before and after the fill pass.</summary>
public class ThemeCoverage
{
    public string ThemeId { get; set; } = "";
    public string Field { get; set; } = "";
    public List<int> Studies { get; set; } = new();
    public List<int> CitedAfterWriting { get; set; } = new();
    public List<int> CitedFinal { get; set; } = new();
    public List<int> NotCited { get; set; } = new();
    public string FillPass { get; set; } = "not needed";
}

/// <summary>The second, independent assignment of studies to themes and its agreement with the first.</summary>
public class SecondCoding
{
    public double? Kappa { get; set; }
    public int CellsCompared { get; set; }
    public int Agreements { get; set; }
    public List<string> Disagreements { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>Everything the thematic synthesis decided, written to thematic-codebook.json and the ledger.</summary>
public class ThematicCodebook
{
    public string Method { get; set; } = "Thematic synthesis (Thomas & Harden, 2008): codes from verified findings, grouped into descriptive themes, one subsection per theme.";
    public List<ThematicCode> Codes { get; set; } = new();
    public List<Theme> Themes { get; set; } = new();
    public List<UncodedStudy> Uncoded { get; set; } = new();

    /// <summary>The research questions no theme answers, with the reason; null when the run has no research questions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<UnansweredQuestion>? Unanswered { get; set; }
    public List<ThemeCoverage> Coverage { get; set; } = new();
    public SecondCoding? SecondCoding { get; set; }

    /// <summary>What the coding and codebook reused from the cache; null when the cache is off.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SynthesisReuse? Reuse { get; set; }

    /// <summary>Included studies cited nowhere in the synthesis or discussion, with the reason.</summary>
    public List<UncodedStudy> NotCited { get; set; } = new();
    public string? FallbackReason { get; set; }

    /// <summary>What code changed in the written text, such as limitations merged into one paragraph; null when nothing was.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? WritingNotes { get; set; }

    [JsonIgnore] public IEnumerable<int> CodedStudies => Themes.SelectMany(t => t.Studies).Distinct().OrderBy(r => r);
}
