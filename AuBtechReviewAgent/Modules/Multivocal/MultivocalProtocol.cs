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
            sb.AppendLine($"- {MultivocalGuidelines.SearchLabel(key)}");
        if (!plan.IsGreyOnly)
            sb.AppendLine("- Formal literature: the bibliographic databases of the systematic review, searched with their own search strings");
        sb.AppendLine();
        sb.AppendLine("Not done, by design:");
        sb.AppendLine("- No general web search engine is used: none offers a search API that is free and allows its results to be kept, which a traceable review needs, so the searches above take its place.");
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
        sb.AppendLine($"Inclusion criteria (G9): {plan.InclusionCriteria}");
        sb.AppendLine($"Exclusion criteria (G9): {(plan.ExclusionCriteria.Length > 0 ? plan.ExclusionCriteria : "none")}");
        sb.AppendLine();
        sb.AppendLine("Formal and grey sources are screened with the same criteria and the same care (G10): each source is screened twice by the language model, independently and with the steps in a different order, and a source the two screenings disagree on is kept and flagged for the reviewer. Sources found twice under different addresses (the same title) are screened once. The reviewer looks at the flagged decisions and may confirm or change any decision before the quality is scored; the model's decision stays on file.");
        sb.AppendLine($"Each grey source is scored on the quality checklist of Table 7, out of {MultivocalGuidelines.QualityPointsMax} points; sources scoring below {plan.QualityThreshold} points are excluded (G11). Each item scores 1, 0.5 or 0. The language model answers the items that need reading the source, and every point it gives rests on a quote found in the kept text of the source's page; the date, the impact and the outlet type are decided in code. Impact uses the counts the search returned: {GreyQualityChecklist.ImpactRule}.");
        sb.AppendLine();
        sb.AppendLine("For an opinion piece, its main claim is recorded word for word with the critical questions for an expert opinion that the guidelines take from Rainer (G13): the writer's expertise, field, assertion and evidence are answered from the page with quotes; the writer's trustworthiness is left to the reviewer, since it cannot be judged reliably, and the claim's consistency with other experts is judged in the synthesis.");
        sb.AppendLine();

        sb.AppendLine("## 6. Systematic map and extraction (G12)");
        sb.AppendLine();
        sb.AppendLine("The language model proposes the attributes of the systematic map from the research questions, with values generalised over the sources that passed the quality check. The reviewer edits the map, every version is kept, and the map is fixed before any source is extracted against it. Every source that passed the quality check is then extracted against the fixed map: each value must rest on a quote found in the kept text of the source's page, and what the page does not state is recorded as not stated. The kind of source, whether it is grey or formal literature, its site, date and quality points, and the research questions it covers are recorded in code.");
        sb.AppendLine();

        sb.AppendLine("## 7. Synthesis (G13)");
        sb.AppendLine();
        sb.AppendLine("For each research question, the language model groups the extracted findings into themes by qualitative coding; every finding goes into a theme or is listed with the reason it fits none, and tensions between sources are named. Every quote is checked again against the kept page text, never the live page. What each theme rests on (how many sources, their outlet tiers and quality points, grey or formal literature) is counted in code, so a theme that rests on 3rd-tier grey literature only is labelled as such.");
        sb.AppendLine();

        sb.AppendLine("## 8. Keeping the evidence");
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
