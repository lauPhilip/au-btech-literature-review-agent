using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// One attribute of the systematic map (G12, Table 10): what is recorded about every source for one research question,
/// with the values it may take, and whether a source can have several of them. An open attribute takes free text
/// (a tool's name, say) because its values cannot be listed in advance.
/// </summary>
public sealed class MapAttribute
{
    public string Name { get; set; } = "";

    /// <summary>The research question it serves, numbered as in the protocol ("RQ1", "RQ1.1").</summary>
    public string Question { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Values { get; set; } = new();
    public bool Multiple { get; set; }
    public bool Open { get; set; }

    /// <summary>
    /// Other research questions this attribute answers too, such as "how often is each practice mentioned?" for the
    /// practice attribute: a question that only counts another attribute's values gets no attribute of its own.
    /// </summary>
    public List<string> AlsoFor { get; set; } = new();

    /// <summary>Whether this attribute answers the question, as its own or as one it answers too.</summary>
    public bool Serves(string question) => Question == question || AlsoFor.Contains(question);
}

/// <summary>One version of the map: who made it and when.</summary>
public sealed class MapVersion
{
    public int Number { get; set; }
    public DateTime CreatedUtc { get; set; }

    /// <summary>"model" (proposed from the questions and the sources) or "reviewer" (edited).</summary>
    public string By { get; set; } = "";
    public List<MapAttribute> Attributes { get; set; } = new();
}

/// <summary>
/// The systematic map of a multivocal run (multivocal-map.json), which is in the run archive: every version, in order,
/// and which one was fixed for the extraction.
/// </summary>
public sealed class MultivocalMapFile
{
    public Guid RunId { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>How many sources the model saw when it generalised the values.</summary>
    public int SourcesSeen { get; set; }

    /// <summary>What code changed in the model's proposal, such as an attribute for a known fact that was left out.</summary>
    public List<string> Notes { get; set; } = new();
    public List<MapVersion> Versions { get; set; } = new();

    /// <summary>The version used for the extraction; null until the reviewer fixes the map.</summary>
    public int? FixedVersion { get; set; }

    public MapVersion Latest => Versions[^1];
}
