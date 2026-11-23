using System;
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
    /// so the fields are numbered as the planner numbers them and the plan on the page is left as typed. Cleaning takes
    /// tag characters out, so what they spelled is read from the plan as typed: call this before the plan is cleaned.
    /// </summary>
    public static IReadOnlyList<InputFlag> Flags(MultivocalPlan plan)
    {
        var clean = JsonSerializer.Deserialize<MultivocalPlan>(JsonSerializer.Serialize(plan))!.Normalized();
        var flags = new List<InputFlag>();
        void Scan(string field, string text, string? typed) =>
            flags.AddRange(ReviewInputGuard.FlagsIn(field, text, ReviewInputGuard.Clean(typed, singleLine: false).Hidden));

        Scan("Topic", clean.Topic, plan.Topic);
        Scan("Goal", clean.Goal, plan.Goal);
        Scan("Existing reviews", clean.ExistingReviews, plan.ExistingReviews);
        for (int i = 0; i < clean.Questions.Count; i++) Scan($"Research question {i + 1}", clean.Questions[i].Text, plan.Questions[i].Text);
        Scan("Inclusion criteria", clean.InclusionCriteria, plan.InclusionCriteria);
        Scan("Exclusion criteria", clean.ExclusionCriteria, plan.ExclusionCriteria);
        ScanList("Grey search string", plan.GreySearchStrings, clean.GreySearchStrings);
        ScanList("Formal search string", plan.FormalSearchStrings, clean.FormalSearchStrings);
        return flags;

        // Cleaning drops empty strings and repeats, so each typed string is matched to the cleaned one it became; a string
        // that was only hidden text became nothing and is reported for the list as a whole.
        void ScanList(string name, List<string> typed, List<string> cleaned)
        {
            var hidden = new string[cleaned.Count + 1];
            foreach (var t in typed)
            {
                var c = ReviewInputGuard.Clean(t, singleLine: true);
                int at = cleaned.FindIndex(s => s.Equals(c.Text, StringComparison.OrdinalIgnoreCase));
                hidden[at < 0 ? cleaned.Count : at] += c.Hidden + "\n";
            }
            for (int i = 0; i < cleaned.Count; i++) flags.AddRange(ReviewInputGuard.FlagsIn($"{name} {i + 1}", cleaned[i], hidden[i] ?? ""));
            flags.AddRange(ReviewInputGuard.FlagsIn($"{name}s", "", hidden[cleaned.Count] ?? ""));
        }
    }

    /// <summary>The flags as the protocol and the plan file write them, one line each.</summary>
    public static List<string> Lines(MultivocalPlan plan) => Flags(plan).Select(f => f.ToString()).ToList();
}
