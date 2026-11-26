using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The language edit (once called the stylistic pass): the model rewrites a written part into plain technical English
/// based on ASD-STE100, at about 80 per cent (issue #599), and code keeps the rewrite only when it changes no number, no
/// citation, no quotation and no technical name. A rewrite with a sentence over <see cref="PlainEnglish.MaxSentenceWords"/>
/// words is sent back once; if it still has one, the text before the edit is kept. The statement on AI use names it.
/// </summary>
public static class StylisticRefinerUtility
{
    /// <summary>Raised whenever the prompt changes; every entry of the ledger records the version that wrote it.</summary>
    public const string PromptVersion = "language-edit-v3";

    private static readonly Lazy<string> Template = new(() => PromptTemplate.Hash(
        Prompt(PromptTemplate.Slot("text")) + "\n" + RetryBlock(new[] { (PromptTemplate.Slot("sentence"), 0) })));

    /// <summary>The fingerprint of the prompt's fixed text, with its retry block, pinned to <see cref="PromptVersion"/> in a test.</summary>
    public static string TemplateSha256 => Template.Value;

    private static string Prompt(string rawText) => $$"""
        You are a rigorous academic copyeditor doing a language edit of a draft manuscript. Edit it into plain technical English, based on ASD-STE100 Simplified Technical English, that still reads as natural, good academic English.

        PLAIN TECHNICAL ENGLISH RULES:
        1. Sentence length: keep a descriptive sentence to 25 words or fewer where you can. A sentence of more than 30 words is a fault: split it into shorter sentences.
        2. Paragraphs: one topic per paragraph, and at most 6 sentences in a paragraph.
        3. Voice: use the active voice ("we screened the records"). The conventional passive of a methods description ("records were screened") is allowed.
        4. Tenses: use simple tenses (the simple present and the simple past).
        5. Verbs, not nominalisations: write "we screened the records", not "screening of the records was performed".
        6. Noun clusters: put at most 3 words in a row that modify one another ("defect prediction model" is the longest allowed). Break a longer cluster up with a preposition ("the accuracy of the defect prediction model").
        7. Complete sentences: never leave out articles ("a", "the"), verbs or other words to make a sentence shorter.
        8. One term per concept: use the same key term for the same concept every time; do not vary it for style.
        9. No filler and no marketing words: delete words and phrases such as "crucially", "importantly", "it is worth noting that", "pivotal", "landscape", "robust", "novel", "cutting-edge", "delve" and "testament", and structures such as "not only ..., but also ...".

        WHAT STAYS FREE:
        Normal academic vocabulary is fine; there is no list of approved words. Vary the rhythm of the sentences and use normal connectives ("however", "because", "in contrast") so the text flows. Use a semicolon only sparingly.

        NEVER CHANGE:
        Citation markers such as [3] or [1, 2] and the claims they support, numbers written as digits, text in quotation marks, the names of tools, models, methods, standards and organisations, and the meaning of any sentence. Do not add content and do not remove content.

        ORIGINAL DRAFT:
        {{rawText}}

        TASK: Edit the draft above into clear, direct academic prose in plain technical English. Retain all factual source references, citations, and specific data points exactly.

        Respond ONLY with the clean, refined academic text paragraph. Do not add introductions, conversational remarks, or markdown code blocks.
        """;

    // Sent with the prompt when the first edit kept a sentence over the limit; the sentences are the model's own edit.
    private static string RetryBlock(IEnumerable<(string Sentence, int Words)> longSentences) =>
        $"\nYOUR FIRST EDIT HAD SENTENCES OF MORE THAN {PlainEnglish.MaxSentenceWords} WORDS. Edit the draft again, and split each of these into shorter sentences, keeping every citation marker with the claim it supports:\n" +
        string.Join("\n", longSentences.Select(l => $"- \"{l.Sentence}\" ({l.Words} words)"));

    public static async Task<(string RefinedText, StyleDeltaLog Delta)> RefineAcademicProseAsync(
        IChatCompletionService chatService,
        string fieldName,
        string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return (string.Empty, new StyleDeltaLog(fieldName, string.Empty, string.Empty));

        StyleDeltaLog Log(string kept, string refined, bool applied = true, string note = "") =>
            new(fieldName, rawText, refined, applied, note, PromptVersion, PlainEnglish.Measure(rawText), PlainEnglish.Measure(kept), FlagsFor(kept));

        string refinedText;
        try
        {
            refinedText = await AskAsync(chatService, Prompt(rawText));
        }
        catch (Exception ex)
        {
            return (rawText, Log(rawText, rawText, false, $"Language edit call failed: {ex.GetType().Name}"));
        }

        // Keep the original and record why, so the ledger shows the rewrite was attempted and refused.
        if (CheckRewrite(rawText, refinedText) is { } rejection) return (rawText, Log(rawText, refinedText, false, rejection));

        var tooLong = PlainEnglish.LongSentences(refinedText);
        if (tooLong.Count == 0) return (refinedText, Log(refinedText, refinedText));

        string first = $"The first edit had {Sentences(tooLong.Count)} over {PlainEnglish.MaxSentenceWords} words ({string.Join(", ", tooLong.Select(l => l.Words))}); it was sent back once.";
        string retried;
        try
        {
            retried = await AskAsync(chatService, Prompt(rawText) + RetryBlock(tooLong));
        }
        catch (Exception ex)
        {
            return (rawText, Log(rawText, refinedText, false, $"{first} The second call failed ({ex.GetType().Name}); the text before the edit was kept."));
        }
        if (CheckRewrite(rawText, retried) is { } again)
            return (rawText, Log(rawText, retried, false, $"{first} {again} The text before the edit was kept."));
        var stillLong = PlainEnglish.LongSentences(retried);
        if (stillLong.Count > 0)
            return (rawText, Log(rawText, retried, false,
                $"{first} The second edit still had {Sentences(stillLong.Count)} over {PlainEnglish.MaxSentenceWords} words ({string.Join(", ", stillLong.Select(l => l.Words))}); the text before the edit was kept."));
        return (retried, Log(retried, retried, true, $"{first} The second edit split them."));
    }

    private static async Task<string> AskAsync(IChatCompletionService chat, string prompt) =>
        (await chat.GetChatMessageContentAsync(prompt)).ToString().Trim();

    private static string Sentences(int n) => n == 1 ? "1 sentence" : $"{n} sentences";

    /// <summary>The flags of the text kept: long noun clusters and paragraphs, and any sentence still over the limit.</summary>
    private static List<string> FlagsFor(string kept) =>
        PlainEnglish.Flags(kept).Concat(PlainEnglish.LongSentences(kept)
            .Select(l => $"Sentence of {l.Words} words (more than {PlainEnglish.MaxSentenceWords}), as before the edit: \"{l.Sentence}\"")).ToList();

    private static readonly Regex Citation = new(@"\[\d+(?:\s*[,–-]\s*\d+)*\]", RegexOptions.Compiled);
    private static readonly Regex Quotation = new(@"""([^""]{3,})""|“([^”]{3,})”", RegexOptions.Compiled);

    // A technical name: a word with two capitals (PRISMA, LangChain) or with a letter and a digit (GPT-4, Llama3).
    private static readonly Regex TechnicalName = new(@"\b(?=[\w-]*\p{Lu}[\w-]*\p{Lu}|[\w-]*\p{L}[\w-]*\d|[\w-]*\d[\w-]*\p{L})[\p{L}\p{N}][\w-]*", RegexOptions.Compiled);

    /// <summary>
    /// A copy-edit should not change what the text says. Reject rewrites that are far longer or shorter than the
    /// original, or that drop or add a number or an inline [n] citation, or drop a quotation or a technical name.
    /// Returns null when the rewrite is acceptable, otherwise the reason it was rejected.
    /// </summary>
    public static string? CheckRewrite(string original, string rewrite)
    {
        if (string.IsNullOrWhiteSpace(rewrite)) return "Rewrite was empty.";

        double ratio = rewrite.Length / (double)Math.Max(1, original.Length);
        if (ratio > 1.6) return $"Rewrite is {ratio:0.0}x the original length; a copy-edit should not add content.";
        if (ratio < 0.4) return $"Rewrite is {ratio:0.0}x the original length; a copy-edit should not remove content.";

        var citesBefore = Citation.Matches(original).Select(m => m.Value).OrderBy(x => x).ToList();
        var citesAfter = Citation.Matches(rewrite).Select(m => m.Value).OrderBy(x => x).ToList();
        if (!citesBefore.SequenceEqual(citesAfter)) return "Rewrite changed the inline [n] citations.";

        // Numbers written as digits (counts, years, percentages) must survive unchanged.
        var numsBefore = Regex.Matches(Regex.Replace(original, @"\[[^\]]*\]", ""), @"\d+(?:[.,]\d+)?").Select(m => m.Value).OrderBy(x => x).ToList();
        var numsAfter = Regex.Matches(Regex.Replace(rewrite, @"\[[^\]]*\]", ""), @"\d+(?:[.,]\d+)?").Select(m => m.Value).OrderBy(x => x).ToList();
        if (!numsBefore.SequenceEqual(numsAfter)) return "Rewrite added, removed or changed a number.";

        string plainRewrite = rewrite.Replace('“', '"').Replace('”', '"');
        foreach (Match q in Quotation.Matches(original))
        {
            string quoted = q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value;
            if (!plainRewrite.Contains(quoted, StringComparison.Ordinal)) return "Rewrite changed a quotation.";
        }

        var namesAfter = TechnicalName.Matches(rewrite).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var lost = TechnicalName.Matches(original).Select(m => m.Value).Where(n => !namesAfter.Contains(n)).Distinct().ToList();
        if (lost.Count > 0) return $"Rewrite dropped or changed a technical name: {string.Join(", ", lost)}.";

        return null;
    }
}
