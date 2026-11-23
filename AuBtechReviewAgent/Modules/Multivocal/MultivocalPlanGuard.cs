using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// Scans what the reviewer typed into the multivocal planning form for phrases that read like instructions to the
/// model, with the same patterns and exceptions as the systematic review's form (#365). The plan's topic, goal,
/// questions and criteria go into every later prompt, so a hit is shown before the protocol is written, the reviewer
/// must confirm it, and the confirmed hits are written to the protocol and the plan. A hit never changes the plan.
/// </summary>
public static class MultivocalPlanGuard
{
    /// <summary>
    /// The flags of every text field of the plan, scanned on a cleaned copy (<see cref="MultivocalPlan.Normalized"/>),
    /// so the fields are numbered as the planner numbers them and the plan on the page is left as typed.
    /// </summary>
    public static IReadOnlyList<InputFlag> Flags(MultivocalPlan plan)
    {
        var clean = JsonSerializer.Deserialize<MultivocalPlan>(JsonSerializer.Serialize(plan))!.Normalized();
        var flags = new List<InputFlag>();
        void Scan(string field, string text) => flags.AddRange(ReviewInputGuard.FlagsIn(field, text));

        Scan("Topic", clean.Topic);
        Scan("Goal", clean.Goal);
        Scan("Existing reviews", clean.ExistingReviews);
        for (int i = 0; i < clean.Questions.Count; i++) Scan($"Research question {i + 1}", clean.Questions[i].Text);
        Scan("Inclusion criteria", clean.InclusionCriteria);
        Scan("Exclusion criteria", clean.ExclusionCriteria);
        for (int i = 0; i < clean.GreySearchStrings.Count; i++) Scan($"Grey search string {i + 1}", clean.GreySearchStrings[i]);
        for (int i = 0; i < clean.FormalSearchStrings.Count; i++) Scan($"Formal search string {i + 1}", clean.FormalSearchStrings[i]);
        return flags;
    }

    /// <summary>The flags as the protocol and the plan file write them, one line each.</summary>
    public static List<string> Lines(MultivocalPlan plan) => Flags(plan).Select(f => f.ToString()).ToList();
}
