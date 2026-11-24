using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

// Writing the paper through the shared writing steps, from the run's report input.
public sealed partial class MultivocalWriter
{
    /// <summary>The writing itself, from the run's files: no file is read or written here, so tests can call it directly.</summary>
    public static async Task<(MultivocalPaperFile File, string Outline)> WritePaperAsync(IChatCompletionService chat, MultivocalReportInput input,
        Func<PageSnapshot, string?> readText, string model, int parallelism, IProgress<string>? progress = null)
    {
        var plan = input.Planned.Plan;
        var (evidence, questionOfTheme) = MultivocalEvidence.Build(input, readText);
        var profile = Profile(plan);
        int referenceCount = evidence.Sources.Count;
        string goal = string.IsNullOrWhiteSpace(plan.Goal) ? plan.Topic : plan.Goal;
        string referenceLines = evidence.ReferenceLines();
        var file = new MultivocalPaperFile { RunId = input.Planned.RunId, WrittenUtc = DateTime.UtcNow, Model = model, AppVersion = RecordingChatCompletionService.AppVersion };
        void Warn(string message)
        {
            file.Notes.Add(message);
            _log.LogWarning("{Message}", message.Replace('\r', ' ').Replace('\n', ' '));
        }
        void Step(int number, string what) => progress?.Report($"{number} of {WritingSteps} writing steps · {what}");

        // 1. The grounded outline: the claims each source supports, before any prose. Each source comes with its verified
        // findings and one excerpt, not two, so the one prompt with every source stays small (lean evidence).
        Step(1, "the grounded outline");
        string excerpts = string.Join("\n", evidence.Sources.Select(s => evidence.SourceBlock(s.Reference, null, OutlineExcerpts, profile.LeanEvidence)));
        string outline;
        using (LlmStage.Begin("outline"))
            outline = await ReviewWriter.WriteOutlineAsync(chat, profile, plan.Topic, goal, excerpts, referenceLines, Warn);

        // 2. One cited section per theme of the questions the model writes about, each with its fill pass.
        Step(2, $"{evidence.Themes.Count} theme section{(evidence.Themes.Count == 1 ? "" : "s")}");
        var (sections, coverage) = await ReviewWriter.WriteThemesAsync(chat, profile, goal, evidence, referenceCount, parallelism,
            (done, total) => progress?.Report($"2 of {WritingSteps} writing steps · theme sections: {done} of {total} written"), Warn);
        file.Coverage = coverage;

        // The results in question order: each question's theme sections, or its answer in code.
        // A theme whose section could not be written stays in its place, said in code, so no theme drops out unseen.
        var themesOf = evidence.Themes.GroupBy(t => questionOfTheme.GetValueOrDefault(t.Id, "")).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (number, question) in plan.Numbered())
        {
            if (MultivocalPaperParts.AnsweredInCode(question.Type))
            {
                var answer = MultivocalPaperParts.CodeAnswer(input, number, question);
                file.Results.Add(new PaperResult { Question = number, QuestionText = question.Text, Kind = "code", Field = $"answer-{number}", Heading = answer.Heading, Text = answer.Text });
                continue;
            }
            var themesHere = themesOf.GetValueOrDefault(number) ?? new List<EvidenceTheme>();
            if (themesHere.Count == 0)
            {
                // Said in code, so the paper never leaves a question out without saying why.
                file.Results.Add(new PaperResult
                {
                    Question = number, QuestionText = question.Text, Kind = "code", Field = $"answer-{number}", Heading = $"{number}: {question.Text}",
                    Text = input.Synthesis.Unanswered.Contains(number)
                        ? "No extracted source states anything this question asks about, so the review cannot answer it from these sources."
                        : "The sources' findings on this question formed no theme that could be written up, so the review does not answer it.",
                });
                continue;
            }
            foreach (var evidenceTheme in themesHere)
            {
                var theme = input.Synthesis.Themes.FirstOrDefault(t => t.Question == number && t.Name == evidenceTheme.Name);
                string? note = theme == null ? null : MultivocalPaperParts.ThemeNote(input, theme);
                var section = sections.FirstOrDefault(s => s.ThemeId == evidenceTheme.Id);
                if (section == null)
                {
                    file.Results.Add(new PaperResult
                    {
                        Question = number, QuestionText = question.Text, Kind = UnwrittenKind,
                        Field = coverage.FirstOrDefault(c => c.ThemeId == evidenceTheme.Id)?.Field ?? $"unwritten-{evidenceTheme.Id}",
                        Heading = $"{number}: {evidenceTheme.Name}",
                        Text = UnwrittenText + (note == null ? "" : " " + note),
                    });
                    continue;
                }
                file.Results.Add(new PaperResult
                {
                    Question = number, QuestionText = question.Text, Kind = "theme", Field = section.Field, Heading = section.Heading, Text = section.Text,
                    Note = note,
                });
            }
        }
        var forDiscussion = file.Results.Select(r => new SynthesisSection { Field = r.Field, Heading = r.Heading, Text = r.Text }).ToList();

        // 3. The discussion, from all the results.
        Step(3, "the discussion");
        string discussion;
        using (LlmStage.Begin("discussion"))
            discussion = await ReviewWriter.WriteDiscussionAsync(chat, profile, plan.Topic, goal, forDiscussion, evidence, referenceCount, Warn);

        // 4. The automated peer review of the theme sections and the discussion, with one revision.
        Step(4, "the automated peer review");
        if (sections.Count > 0 && discussion.Length > 0)
        {
            using (LlmStage.Begin("automated-peer-review"))
                (file.PeerReview, discussion) = await ReviewWriter.PeerReviewAsync(chat, profile, sections, discussion, evidence, referenceCount, parallelism, Warn);
            foreach (var result in file.Results.Where(r => r.Kind == "theme"))
                result.Text = sections.FirstOrDefault(s => s.Field == result.Field)?.Text ?? result.Text;
            forDiscussion = file.Results.Select(r => new SynthesisSection { Field = r.Field, Heading = r.Heading, Text = r.Text }).ToList();
        }
        file.Discussion = discussion;

        // 5. The summary for practitioners (G14), from the checked-to-be results and the discussion only.
        Step(5, "the summary for practitioners");
        var summary = await ReviewWriter.WritePractitionerSummaryAsync(chat, profile, plan.Topic, goal, MultivocalReporter.Audience(plan.Audience),
            forDiscussion, discussion, referenceLines, referenceCount, Warn);
        file.SummaryLead = summary?.Lead;
        file.SummaryItems = summary?.Items.ToList() ?? new List<string>();

        // 6. Last, the title, abstract, rationale and objectives, with the numbers counted in code; then the style pass.
        Step(6, "the title, abstract and introduction");
        var front = await ReviewWriter.WriteFrontMatterAsync(chat, profile, plan.Topic, goal, FrontMatterFacts(input, sections.Count), forDiscussion, discussion,
            referenceLines, referenceCount, Warn, abstractRule: text => MultivocalFrontMatter.AbstractProblem(text, input));
        file.Title = front?.Title ?? $"{plan.Topic}: a {plan.ReviewKind}";
        file.Style.Add(new StyleDeltaLog("Title", file.Title, file.Title, Applied: false, Note: "The title is not sent through the style pass." + (front == null ? " The front-matter step failed; the title is the topic and the kind of review." : "")));
        using (LlmStage.Begin("style"))
        {
            async Task<string> Refine(string field, string? text)
            {
                if (string.IsNullOrWhiteSpace(text)) return "";
                var (refined, delta) = await StylisticRefinerUtility.RefineAcademicProseAsync(chat, field, text);
                file.Style.Add(delta);
                return string.IsNullOrWhiteSpace(refined) ? text : refined;
            }
            file.Abstract = await Refine("Abstract", front?.Abstract);
            file.Rationale = ReviewWriter.CitedSentencesOnly(await Refine("Rationale", front?.Rationale), out var uncited);
            if (uncited != null) file.Style.Add(uncited);
            file.Objectives = await Refine("Objectives", front?.Objectives);
        }
        if (front == null) file.Objectives = $"The review set out to {LowerFirst(goal.TrimEnd('.'))}.";
        return (file, outline);
    }

    /// <summary>How many excerpts of each source's text the outline prompt gets; the theme prompts choose their own.</summary>
    public const int OutlineExcerpts = 1;

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
