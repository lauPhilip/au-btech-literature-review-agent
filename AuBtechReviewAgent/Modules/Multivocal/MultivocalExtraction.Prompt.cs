using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// The extraction prompt of the multivocal review, and the fingerprint of its fixed text that is part of the cache key.
public sealed partial class MultivocalExtractor
{
    /// <summary>
    /// The extraction prompt: every attribute of the fixed map, each value with a word-for-word quote. With
    /// <paramref name="selected"/>, the text is selected passages of a long page or full text.
    /// </summary>
    public static string Prompt(string topic, MapVersion map, GreyRecord record, string text, bool selected)
    {
        string attributes = string.Join("\n", map.Attributes.Select(a =>
            $"- {a.Name} ({a.Question}; {(a.Open ? "open text" : a.Multiple ? "one or more of: " + string.Join(" | ", a.Values) : "exactly one of: " + string.Join(" | ", a.Values))}): {a.Description}"));
        string about = $"""
            - Title: {record.Title}
            - Address: {record.Url}
            - Published on: {record.Site}
            """;
        // A formal source (block I-5) is named as academic, and its text is its open full text or its abstract.
        bool formal = record.Kind == MultivocalSearcher.FormalKind;
        string kind = formal ? "academic source (a paper from a bibliographic database)" : "grey literature source";
        string textHeading = formal ? "SOURCE TEXT (its open full text or selected passages of it, or its abstract when no full text is open; the only evidence you may use)"
            : selected ? "PAGE TEXT (selected passages of a long page: its start and the passages most relevant to the map; [passage break] marks text left out; the only evidence you may use)"
            : "PAGE TEXT (the only evidence you may use)";
        return $$"""
            You extract data from one {{kind}} for a multivocal literature review (Garousi et al. 2019, G12), against the review's systematic map.
            The review is about: {{PromptSafety.Wrap(topic, "review topic")}}

            {{PromptSafety.ReviewerInputNotice}}
            {{PromptSafety.DataOnlyNotice}}

            SOURCE:
            {{PromptSafety.Wrap(about, "source metadata")}}

            {{textHeading}}:
            {{PromptSafety.Wrap(text, formal ? "source text" : "page text")}}

            ATTRIBUTES OF THE MAP (from the reviewer):
            {{PromptSafety.Wrap(attributes, "map attributes")}}

            For every attribute, give the values this page states, each with a short passage copied word for word from the page text that shows it.
            Use only the listed values for an attribute with a list; use "Other" only when it is listed and nothing else fits. Give at most one value where it says "exactly one".
            For an open-text attribute, give each name as its own value, never a list of names in one value. When it asks for tools, frameworks or platforms, name the software only: not language models, programming languages or methods.
            When the page says nothing for an attribute, give no values for it: "not stated" is recorded automatically. Never guess.
            Also say in one sentence what the source sets out to do (its purpose), with a word-for-word quote.

            {{AnswerShape}}
            """;
    }

    /// <summary>Asks the model for the extraction of one source with <see cref="Prompt"/>, checked against the map in code.</summary>
    public static async Task<ModelExtraction> AskAsync(IChatCompletionService chat, MultivocalPlan plan, MapVersion map, GreyRecord record, string text, bool selected = false)
    {
        string prompt = Prompt(plan.Topic, map, record, text, selected);
        return await LlmJson.GetAsync<ModelExtraction>(chat, prompt, LlmJson.JsonMode(0.0), a => ModelExtraction.Problem(a, map));
    }

    private const string AnswerShape = """
        Respond ONLY with a valid minified JSON object of this shape, with one entry per attribute:
        {"purpose":{"value":"One sentence.","quote":"Word for word from the page."},"attributes":[{"attribute":"Practice","values":[{"value":"Retrieval","quote":"Word for word from the page."}]}]}
        """;

    private static readonly Lazy<string>[] Templates = { new(() => PromptTemplate.Hash(Template(false))), new(() => PromptTemplate.Hash(Template(true))) };

    /// <summary>
    /// The fingerprint of the extraction prompt's fixed text, for a grey page (whole and as selected passages) or a formal
    /// source, part of the cache key, so an edit to the prompt is never answered from the cache.
    /// </summary>
    public static string TemplateSha256(bool formal) => Templates[formal ? 1 : 0].Value;

    /// <summary>
    /// The extraction prompt written with placeholders, with a map of one attribute of each kind (open, one value, several
    /// values) so the way an attribute is described is part of the fixed text.
    /// </summary>
    public static IEnumerable<string> Template(bool formal)
    {
        string S(string name) => PromptTemplate.Slot(name);
        var map = new MapVersion { Attributes = {
            new MapAttribute { Name = S("open"), Question = S("question"), Description = S("description"), Open = true },
            new MapAttribute { Name = S("one"), Question = S("question"), Description = S("description"), Values = { S("a"), S("b") } },
            new MapAttribute { Name = S("several"), Question = S("question"), Description = S("description"), Values = { S("a"), S("b") }, Multiple = true },
        } };
        var record = new GreyRecord(S("id"), S("title"), S("summary"), S("url"), S("producer"), S("site"),
            formal ? MultivocalSearcher.FormalKind : S("kind"), null, new Dictionary<string, long>());
        yield return Prompt(S("topic"), map, record, S("text"), false);
        if (!formal) yield return Prompt(S("topic"), map, record, S("text"), true);
    }
}
