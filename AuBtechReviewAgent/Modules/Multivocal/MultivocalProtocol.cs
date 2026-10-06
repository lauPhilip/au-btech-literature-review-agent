using System;
using System.Linq;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// The protocol of a multivocal review (G1): the planning answers in the order of the paper's Fig. 7, written in code
/// (never by the model) before anything is searched. Its SHA-256 is recorded with the plan, so any later change to
/// the protocol can be seen. What the review deliberately does not do (decisions 1, 3 and 5 in
/// docs/roadmap/mlr-todo.md) is stated here too, so a reader does not have to guess.
/// </summary>
public static class MultivocalProtocol
{
    public static string Write(MultivocalPlan plan, Guid runId, DateTime createdUtc)
    {
        var sb = new StringBuilder();
        string kind = plan.IsGreyOnly ? "Grey Literature Review" : "Multivocal Literature Review";
        sb.AppendLine($"# Review protocol: {kind}");
        sb.AppendLine();
        sb.AppendLine($"- Run: {runId}");
        sb.AppendLine($"- Written: {createdUtc:yyyy-MM-dd HH:mm} UTC, before any search");
        sb.AppendLine($"- Review type: {kind}, following {MultivocalGuidelines.Reference}");
        sb.AppendLine($"- Sources reviewed: {(plan.IsGreyOnly ? "grey literature only" : "formal (academic) and grey literature, kept apart in two pools")}");
        sb.AppendLine();

        sb.AppendLine("## 1. Need and audience (G2)");
        sb.AppendLine();
        sb.AppendLine($"- Topic: {plan.Topic}");
        sb.AppendLine($"- Goal: {plan.Goal}");
        sb.AppendLine($"- Intended audience: {Audience(plan.Audience)}");
        sb.AppendLine($"- Existing reviews: {(plan.ExistingReviews.Length > 0 ? plan.ExistingReviews : "none named")}");
        sb.AppendLine();

        sb.AppendLine("## 2. Including grey literature (G3, Table 4)");
        sb.AppendLine();
        for (int i = 0; i < MultivocalGuidelines.IncludeGreyQuestions.Count; i++)
        {
            string answer = plan.IncludeGrey.ElementAtOrDefault(i) switch { true => "Yes", false => "No", null => "Not answered" };
            sb.AppendLine($"{i + 1}. {MultivocalGuidelines.IncludeGreyQuestions[i]} {answer}");
        }
        sb.AppendLine();
        int yes = plan.IncludeGrey.Count(a => a == true);
        sb.AppendLine(yes > 0
            ? $"{yes} of {MultivocalGuidelines.IncludeGreyQuestions.Count} answers are \"yes\", which suggests including grey literature."
            : "No answer is \"yes\": by the guidelines, grey literature is not needed and a systematic review may be enough. The reviewer chose to go ahead with grey literature.");
        sb.AppendLine();

        sb.AppendLine("## 3. Research questions (G4, G5)");
        sb.AppendLine();
        foreach (var (number, q) in plan.Numbered())
        {
            var type = MultivocalGuidelines.FindType(q.Type);
            string indent = q.Parent != null ? "  " : "";
            sb.AppendLine($"{indent}- {number}: {q.Text} (type: {type?.Name ?? "not set"}{(type is { HardForSources: true } ? "; sources may not allow this type to be answered" : "")})");
        }
        sb.AppendLine();

        sb.AppendLine("## 4. Search (G6–G8)");
        sb.AppendLine();
        sb.AppendLine("Kinds of grey literature covered (G6):");
        foreach (var key in plan.GreyTypes)
            sb.AppendLine($"- {MultivocalGuidelines.GreyTypes.First(t => t.Key == key).Label}");
        sb.AppendLine();
        sb.AppendLine("Where grey literature is searched (G7):");
        foreach (var key in plan.GreySearches)
            sb.AppendLine($"- {MultivocalGuidelines.GreySearches.First(s => s.Key == key).Label}");
        if (!plan.IsGreyOnly)
            sb.AppendLine("- Formal literature: the bibliographic databases of the systematic review, searched with their own search strings");
        sb.AppendLine();
        sb.AppendLine("Not done, by design:");
        if (!plan.GreySearches.Contains("brave"))
            sb.AppendLine("- No general web search engine is used: no general web search API is both free and stable, so the searches above take its place.");
        if (!plan.GreyTypes.Contains("talks"))
            sb.AppendLine("- Talks and videos are not covered.");
        else
            sb.AppendLine("- A talk or video counts only when its transcript is published with it; quotes are checked against that transcript.");
        sb.AppendLine("- Practitioners and authors are not contacted for sources; only sources found by the searches are reviewed.");
        sb.AppendLine();
        sb.AppendLine(plan.GreySearchStrings.Count > 0 ? "Search strings for grey literature (G7):" : "Search string for grey literature (G7), the topic:");
        foreach (var text in plan.EffectiveGreySearchStrings)
            sb.AppendLine($"- {text}");
        sb.AppendLine();
        var rule = MultivocalGuidelines.StoppingRules.First(r => r.Key == plan.StoppingRule);
        sb.AppendLine($"Stopping rule (G8): {rule.Label}{(plan.StoppingRule == "effort" ? $", the top {plan.TopHits} hits of each search string" : "")}.");
        sb.AppendLine();

        sb.AppendLine("## 5. Selection and quality (G9–G11)");
        sb.AppendLine();
        sb.AppendLine("Formal and grey sources are screened with the same criteria and the same care (G10).");
        sb.AppendLine($"Each grey source is scored on the quality checklist of Table 7, out of {MultivocalGuidelines.QualityPointsMax} points; sources scoring below {plan.QualityThreshold} points are excluded (G11). Every score rests on a quote from the source.");
        sb.AppendLine();

        sb.AppendLine("## 6. Keeping the evidence");
        sb.AppendLine();
        sb.AppendLine("The text of each web source is kept on the server for the run's lifetime, so every citation check can be repeated. The run archive holds each source's address, access date, SHA-256 fingerprint, the quoted passages and a Wayback Machine link, not the pages themselves.");
        return sb.ToString();
    }

    private static string Audience(ReviewAudience? audience) => audience switch
    {
        ReviewAudience.Researchers => "researchers",
        ReviewAudience.Practitioners => "practitioners",
        ReviewAudience.Both => "researchers and practitioners",
        _ => "not set",
    };
}
