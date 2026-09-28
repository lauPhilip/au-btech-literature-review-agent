using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0070
using Microsoft.SemanticKernel.Connectors.MistralAI;

namespace AuBtechReviewAgent;

/// <summary>One extracted fact with the sentence from the paper that supports it.</summary>
public class EvidencedValue
{
    public string Value { get; set; } = "";
    public string Quote { get; set; } = "";
    /// <summary>True when the quote was found word for word in the paper text the model was shown.</summary>
    public bool QuoteVerified { get; set; }
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

    [JsonIgnore] public int AppraisalYes => Appraisal.Count(a => a.Answer == "yes");
}

/// <summary>
/// Structured data extraction and quality appraisal per included study, the two PRISMA steps between
/// screening and synthesis that the pipeline was missing. For each paper the model reads the abstract and the
/// most relevant passages and fills an extraction form (study type, method, sample, key findings,
/// limitations) and the Mixed Methods Appraisal Tool (MMAT, version 2018) criteria for the study's category.
/// Every value must come with a verbatim quote, and each quote is checked against the text in code.
/// MMAT is not scored into a single number (its authors advise against that); the criteria are reported.
/// </summary>
public static class StudyExtractor
{
    public static readonly IReadOnlyDictionary<string, string[]> MmatCriteria = new Dictionary<string, string[]>
    {
        ["qualitative"] = new[]
        {
            "1.1 The qualitative approach is appropriate to answer the research question",
            "1.2 The data collection methods are adequate to address the research question",
            "1.3 The findings are adequately derived from the data",
            "1.4 The interpretation of results is sufficiently substantiated by data",
            "1.5 There is coherence between data sources, collection, analysis and interpretation",
        },
        ["quantitative_randomized"] = new[]
        {
            "2.1 Randomization is appropriately performed",
            "2.2 The groups are comparable at baseline",
            "2.3 There are complete outcome data",
            "2.4 Outcome assessors are blinded to the intervention provided",
            "2.5 Participants adhered to the assigned intervention",
        },
        ["quantitative_nonrandomized"] = new[]
        {
            "3.1 The participants are representative of the target population",
            "3.2 Measurements are appropriate regarding both the outcome and the intervention (or exposure)",
            "3.3 There are complete outcome data",
            "3.4 The confounders are accounted for in the design and analysis",
            "3.5 The intervention was administered (or exposure occurred) as intended",
        },
        ["quantitative_descriptive"] = new[]
        {
            "4.1 The sampling strategy is relevant to address the research question",
            "4.2 The sample is representative of the target population",
            "4.3 The measurements are appropriate",
            "4.4 The risk of nonresponse bias is low",
            "4.5 The statistical analysis is appropriate to answer the research question",
        },
        ["mixed_methods"] = new[]
        {
            "5.1 There is an adequate rationale for using a mixed methods design",
            "5.2 The different components of the study are effectively integrated",
            "5.3 The outputs of the integration of components are adequately interpreted",
            "5.4 Divergences and inconsistencies between components are adequately addressed",
            "5.5 The different components adhere to the quality criteria of each method involved",
        },
    };

    private class ExtractionAnswer
    {
        public string StudyType { get; set; } = "";
        public EvidencedValue? Method { get; set; }
        public EvidencedValue? Sample { get; set; }
        public List<EvidencedValue>? KeyFindings { get; set; }
        public EvidencedValue? Limitations { get; set; }
        public string AppraisalCategory { get; set; } = "";
        public List<AppraisalAnswer>? Appraisal { get; set; }
    }

    public static async Task<StudyExtraction> ExtractAsync(IChatCompletionService chat, ReferencedPaper paper, int excerpts = 8)
    {
        var result = new StudyExtraction { ReferenceNumber = paper.ReferenceNumber, PaperId = paper.PaperId, Title = paper.Title,
            EvidenceBasis = paper.HasFullText ? "full text" : "abstract only" };

        var terms = TextRelevance.Terms("method methodology approach design sample participants dataset case study experiment evaluation results findings show limitations threats validity");
        var chunks = GroundingContextBuilder.SelectChunks(paper.Chunks, terms, excerpts);
        var evidence = new StringBuilder();
        evidence.AppendLine($"Abstract: {paper.Abstract}");
        foreach (var c in chunks) evidence.AppendLine($"[page {c.PageNumber}] {c.Text}");
        string evidenceText = evidence.ToString();

        string criteriaList = string.Join("\n", MmatCriteria.Select(kv => $"- {kv.Key}: " + string.Join(" | ", kv.Value)));
        string prompt = $$"""
            You are extracting data from one study for a systematic literature review, and appraising its quality with the Mixed Methods Appraisal Tool (MMAT, 2018).

            {{PromptSafety.DataOnlyNotice}}

            STUDY: [{{paper.ReferenceNumber}}] {{paper.Title}}
            {{PromptSafety.Wrap(evidenceText, "paper text: abstract and selected passages")}}

            TASK 1 - EXTRACTION. Fill each field from the text above. Every value must have a verbatim quote (one sentence, max 40 words) copied exactly from the text. If the text does not say, use value "not reported" and an empty quote.
            - studyType: one short phrase (e.g. "design science artefact with case study", "controlled experiment", "survey", "literature review", "position paper").
            - method, sample, limitations: one sentence each.
            - keyFindings: 1 to 3 findings.

            TASK 2 - APPRAISAL. First decide if the study is empirical (it has a research question and collects or analyses data to answer it). If not (e.g. a position paper, a system description without evaluation, or a literature review), set appraisalCategory to "not_empirical" and appraisal to []. Otherwise choose ONE category and answer its five criteria with "yes", "no" or "cant_tell", each with a verbatim quote (empty quote only for "cant_tell").
            {{criteriaList}}

            Respond ONLY with a minified JSON object:
            {"studyType":"...","method":{"value":"...","quote":"..."},"sample":{"value":"...","quote":"..."},"keyFindings":[{"value":"...","quote":"..."}],"limitations":{"value":"...","quote":"..."},"appraisalCategory":"...","appraisal":[{"id":"3.1","answer":"yes","quote":"..."}]}
            """;

        var settings = new MistralAIPromptExecutionSettings { Temperature = 0.0 };
        settings.ExtensionData ??= new Dictionary<string, object>();
        settings.ExtensionData["response_format"] = new { type = "json_object" };

        try
        {
            var answer = await LlmJson.GetAsync<ExtractionAnswer>(chat, prompt, settings, a =>
            {
                if (string.IsNullOrWhiteSpace(a.StudyType)) return "studyType is missing.";
                if (a.AppraisalCategory != "not_empirical" && !MmatCriteria.ContainsKey(a.AppraisalCategory))
                    return $"appraisalCategory must be one of: {string.Join(", ", MmatCriteria.Keys)}, not_empirical.";
                if (a.Appraisal != null && a.Appraisal.Any(x => !LlmJson.OneOf(x.Answer, "yes", "no", "cant_tell")))
                    return "each appraisal answer must be yes, no or cant_tell.";
                return null;
            });

            result.StudyType = answer.StudyType.Trim();
            result.Method = Verify(answer.Method, evidenceText);
            result.Sample = Verify(answer.Sample, evidenceText);
            result.Limitations = Verify(answer.Limitations, evidenceText);
            result.KeyFindings = (answer.KeyFindings ?? new()).Take(3).Select(f => Verify(f, evidenceText)).ToList();
            result.AppraisalCategory = answer.AppraisalCategory;

            if (MmatCriteria.TryGetValue(answer.AppraisalCategory, out var criteria))
            {
                for (int i = 0; i < criteria.Length; i++)
                {
                    string id = criteria[i].Split(' ')[0];
                    var given = answer.Appraisal?.FirstOrDefault(x => x.Id.Trim() == id) ?? answer.Appraisal?.ElementAtOrDefault(i);
                    var a = new AppraisalAnswer { Id = id, Criterion = criteria[i][(id.Length + 1)..], Answer = given?.Answer.ToLowerInvariant() ?? "cant_tell", Quote = given?.Quote ?? "" };
                    a.QuoteVerified = CitationSupportChecker.QuoteOccursIn(a.Quote, evidenceText);
                    // A "yes" or "no" must be backed by text that is really in the paper; otherwise it cannot be told.
                    if (a.Answer != "cant_tell" && !a.QuoteVerified) a.Answer = "cant_tell";
                    result.Appraisal.Add(a);
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = $"Extraction failed: {ex.Message}";
        }
        return result;
    }

    private static EvidencedValue Verify(EvidencedValue? v, string evidenceText)
    {
        var value = v ?? new EvidencedValue { Value = "not reported" };
        value.Value = (value.Value ?? "").Trim();
        value.Quote = (value.Quote ?? "").Trim();
        value.QuoteVerified = CitationSupportChecker.QuoteOccursIn(value.Quote, evidenceText);
        return value;
    }
}
