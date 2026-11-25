using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>One value of one attribute for one source, with the quote from the page that shows it.</summary>
public sealed class ExtractedValue
{
    public string Value { get; set; } = "";
    public string Quote { get; set; } = "";
}

/// <summary>What one source says for one attribute of the map.</summary>
public sealed class ExtractedAttribute
{
    public string Attribute { get; set; } = "";

    /// <summary>The values whose quote was found in the page.</summary>
    public List<ExtractedValue> Values { get; set; } = new();

    /// <summary>The values the model gave whose quote was not found in the page; kept on file, never used.</summary>
    public List<ExtractedValue> Unsupported { get; set; } = new();

    /// <summary>The values of an open attribute that name one of the review's own included sources; kept on file, never counted.</summary>
    public List<ExtractedValue> OwnSource { get; set; } = new();

    /// <summary>The values of an attribute about tools that are not a tool (<see cref="ToolNameRules.NotATool"/>); kept on file, never counted.</summary>
    public List<ExtractedValue> NotATool { get; set; } = new();

    /// <summary>True when the source states nothing for this attribute (no supported value).</summary>
    public bool NotStated => Values.Count == 0;
}

/// <summary>The extraction of one source against the fixed map.</summary>
public sealed class GreyExtraction
{
    public string Address { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string TextSha256 { get; set; } = "";

    /// <summary>The facts the run already knows, recorded by code (<see cref="MultivocalMapper.KnownFacts"/>).</summary>
    public List<KnownFact> Known { get; set; } = new();

    /// <summary>What the source sets out to do (G12), with its quote; null when the model's quote was not in the page.</summary>
    public ExtractedValue? Purpose { get; set; }

    /// <summary>The research questions the source informs: those with at least one supported value, decided in code.</summary>
    public List<string> Coverage { get; set; } = new();
    public List<ExtractedAttribute> Attributes { get; set; } = new();
    public string? Error { get; set; }
    public bool FromCache { get; set; }

    /// <summary>A formal source (block I-5), extracted from its kept full text or abstract; left out for a grey source.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Formal { get; set; }

    /// <summary>A formal source's text basis, "full text" or "abstract only" (decision 8); null for a grey source.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TextBasis { get; set; }

    /// <summary>
    /// What of a long full text or page the model was given, when not the whole kept text (<see cref="FormalExtractionText"/>,
    /// <see cref="GreyPageText.ForExtraction"/>), e.g. "14 of 83 passages of the full text (8,104 of 41,230 characters)"; null when it was given the whole text.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Passages { get; set; }
}

/// <summary>A fact about a source that code records.</summary>
public sealed class KnownFact
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>The extraction of a multivocal run (multivocal-extraction.json), which is in the run archive.</summary>
public sealed class MultivocalExtractionFile
{
    public Guid RunId { get; set; }
    public DateTime ExtractedUtc { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>The map version every source was extracted against.</summary>
    public int MapVersion { get; set; }
    public List<GreyExtraction> Sources { get; set; } = new();

    /// <summary>The names of open attributes merged in code (<see cref="OpenNameMerger"/>); empty for runs made before #249.</summary>
    public List<NameMerge> NameMerges { get; set; } = new();

    /// <summary>The values of open attributes left out because they name one of the review's own sources.</summary>
    public List<OwnSourceValue> OwnSourceValues { get; set; } = new();

    /// <summary>The rules that split, matched and left out open values (<see cref="ToolNameRules.Version"/>); null for runs made before #466.</summary>
    public string? NameRules { get; set; }

    /// <summary>The values of open attributes that listed several names, with the names they were split into (#466).</summary>
    public List<SplitValue> SplitValues { get; set; } = new();

    /// <summary>The values of attributes about tools that were not counted, each with why (#466).</summary>
    public List<NotToolValue> NotToolValues { get; set; } = new();

    /// <summary>How many sources have each value of an attribute: the systematic map's counts (G12), for RQs about frequency.</summary>
    public IReadOnlyList<(string Value, int Sources)> Counts(string attribute)
    {
        var extracted = Sources.Where(s => s.Error == null).Select(s => s.Attributes.FirstOrDefault(a => a.Attribute == attribute)).ToList();
        var counts = extracted.Where(a => a != null).SelectMany(a => a!.Values.Select(v => v.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Value: g.Key, Sources: g.Count()))
            .OrderByDescending(x => x.Sources).ThenBy(x => x.Value)
            .ToList();
        int notStated = extracted.Count(a => a == null || a.NotStated);
        if (notStated > 0) counts.Add(("not stated", notStated));
        return counts;
    }
}

/// <summary>Spellings of one name merged in code: the name kept and the spellings it replaced.</summary>
public sealed class NameMerge
{
    public string Attribute { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Merged { get; set; } = new();
}

/// <summary>A value of an open attribute that names one of the review's own included sources, with that source's title.</summary>
public sealed class OwnSourceValue
{
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>A value of an open attribute that listed several names, and the names it was split into.</summary>
public sealed class SplitValue
{
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public List<string> Parts { get; set; } = new();
}

/// <summary>A value of an attribute about tools that is not a tool, with the rule's reason ("language model", ...).</summary>
public sealed class NotToolValue
{
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public string Reason { get; set; } = "";
}
