using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>The discussion, written from the finished theme subsections.</summary>
public static partial class ReviewWriter
{
    /// <summary>The discussion, written from the theme subsections; empty when it cannot be written.</summary>
    public static async Task<string> WriteDiscussionAsync(IChatCompletionService chat, WritingProfile profile, string topic, string goal,
        IReadOnlyList<SynthesisSection> sections, ReviewEvidence evidence, int referenceCount, Action<string>? warn = null)
    {
        int paragraphs = Math.Clamp(2 + sections.Count / 2, 3, 6);
        string synthesis = string.Join("\n\n", sections.Select(x => $"### {x.Heading}\n{x.Text}"));
        string prompt = $$"""
            You are writing {{profile.DiscussionSection}} of {{profile.Review}} whose results are reported theme by theme below.

            REVIEW OBJECTIVE: "{{goal}}"
            PRIMARY TOPIC: "{{topic}}"
            {{PromptSafety.ReviewerInputNotice}}

            REFERENCE LIST (cite ONLY these numbers):
            {{evidence.ReferenceLines()}}

            FACTS ABOUT THE EVIDENCE BASE ({{profile.BaseFactsFrom}}; use them wherever you describe the {{profile.Sources}} or how they were studied, and do not change them):
            {{evidence.BaseFacts}}{{LeftOutBlock(evidence, profile)}}

            RESULTS BY THEME:
            {{synthesis}}

            REQUIREMENTS:
            1. Write about {{paragraphs}} paragraphs, separated by a blank line: {{profile.DiscussionParts}}
            2. Interpret, do not repeat the results. Ground every claim in the results above; every sentence that attributes something to the literature ends with an inline marker such as [3] or [2, 5], using the same {{profile.Sources}} the results cite for it. {{OwnCountRule}}
            3. Do not invent numbers, statistics or {{profile.Sources}}.
            {{ProseCleaner.PlainProseRule}}
            Respond ONLY with a valid minified JSON object:
            {"text":"the discussion with inline [n] citations"}
            """;
        try
        {
            var answer = await LlmJson.GetAsync<TextAnswer>(chat, prompt, LlmJson.JsonMode(0.3), a =>
            {
                if (string.IsNullOrWhiteSpace(a.Text)) return "text must not be empty.";
                var outside = ThematicSynthesis.CitedIn(a.Text).Where(r => r < 1 || r > referenceCount).ToList();
                if (outside.Count > 0) return $"citation numbers {string.Join(", ", outside)} are not in the reference list.";
                if (LeftOutCited(a.Text, evidence) is { } leftOut) return leftOut;
                return OwnCountCited(a.Text, evidence.BaseFacts, synthesis) is { } own
                    ? $"\"{own}\" states this review's own count and carries a citation; the review's own counts take no citation."
                    : null;
            });
            return MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Discussion could not be written: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// The sources the synthesis left out of every theme, for the discussion and the closing sections (issue #166), so
    /// they do not lean on a study the review itself set aside. Empty when none was left out, so the prompt is as before.
    /// </summary>
    public static string LeftOutBlock(ReviewEvidence evidence, WritingProfile profile) => evidence.LeftOut.Count == 0 ? "" : $"""


        {profile.Sources.ToUpperInvariant()} LEFT OUT OF THE THEMES (in the reference list, but the synthesis did not use them; do not cite them as evidence for any claim):
        {string.Join("\n", evidence.LeftOut.Select(u => $"[{u.Reference}] {u.Reason}"))}
        """;

    /// <summary>
    /// Why a text that cites a source left out of the themes cannot be used, or null (issue #166). Public for unit tests.
    /// </summary>
    public static string? LeftOutCited(string text, ReviewEvidence evidence)
    {
        if (evidence.LeftOut.Count == 0) return null;
        var cited = ThematicSynthesis.CitedIn(text);
        var used = evidence.LeftOut.Where(u => cited.Contains(u.Reference)).Select(u => $"[{u.Reference}]").ToList();
        return used.Count == 0 ? null
            : $"it cites {string.Join(", ", used)}, which the synthesis left out of the themes; do not use {(used.Count == 1 ? "it" : "them")} as evidence.";
    }
}
