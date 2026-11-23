using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace AuBtechReviewAgent.Modules.Multivocal.Pages;

// The planning form: its five steps, what is missing in each, the academic databases and their search strings, and
// writing the protocol.
public partial class MultivocalStart
{
    internal sealed record Step(string Title, string Question, string Guideline, string NextLabel);

    internal static readonly Step[] Steps =
    {
        new("Need", "What is the review for, and who reads it?", "G2", "Need and audience"),
        new("Grey", "Is grey literature needed?", "G3", "Grey literature"),
        new("Questions", "What should the review answer?", "G4–G5", "Questions"),
        new("Search", "What to search, where, how much, and what to include?", "G6–G9, G11", "Search"),
        new("Protocol", "Check the plan. Starting the review writes it down, then runs every step on its own.", "G1", "Protocol"),
    };

    internal IReadOnlyList<string> Problems => plan.Problems();

    // Instruction-like text in the plan (#365): shown on the last step, and the review starts only once the reviewer
    // confirms exactly these flags; editing the plan so the flags change asks again.
    internal IReadOnlyList<string> FlagLines => MultivocalPlanGuard.Lines(plan);

    // Signs of broad criteria, checked in code (MultivocalCriteriaCheck); a warning only, never a problem.
    internal IReadOnlyList<string> CriteriaWarnings => MultivocalCriteriaCheck.Warnings(plan);
    private string? confirmedFlags;
    internal bool FlagsConfirmed => FlagLines.Count == 0 || confirmedFlags == string.Join("\n", FlagLines);
    internal void ConfirmFlags(ChangeEventArgs e) => confirmedFlags = e.Value is true ? string.Join("\n", FlagLines) : null;
    internal string ProtocolPreview =>
        MultivocalProtocol.Write(plan, Guid.Empty, DateTime.UtcNow).Replace(Guid.Empty.ToString(), "assigned when the protocol is written");

    // Which academic databases can be searched: the keyless ones always, Scopus and IEEE when the server has a key
    // (decision 7). Read once; the key itself never reaches the page or the plan.
    private IReadOnlyDictionary<string, bool>? formalAvailability;
    private bool formalStarted;
    internal bool proposingStrings;
    internal int proposalsLeft = 5; // each proposal is one model call; a page gets a few, as on the systematic form
    internal string? proposalError;

    internal bool FormalAvailable(string key)
    {
        formalAvailability ??= ReviewEngine.GetSourceAvailability(null);
        return formalAvailability.TryGetValue(key, out bool usable) && usable;
    }

    // A new multivocal plan starts with every database that can be searched ticked, once.
    private void StartFormalPool()
    {
        if (formalStarted || plan.IsGreyOnly) return;
        formalStarted = true;
        if (plan.FormalDatabases.Count == 0)
            plan.FormalDatabases = SourceCatalog.AllKeys.Where(FormalAvailable).ToList();
    }

    internal List<string> UnusedProposals => plan.FormalStringsProposed
        .Where(p => !plan.FormalSearchStrings.Any(t => t.Trim().Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();

    internal void AddSearchString()
    {
        // The first string starts as the topic, so the reviewer edits it instead of typing it again.
        plan.GreySearchStrings.Add(plan.GreySearchStrings.Count == 0 ? plan.Topic.Trim() : "");
    }

    internal void AddFormalSearchString()
    {
        // The first string starts as the topic, as for grey literature.
        plan.FormalSearchStrings.Add(plan.FormalSearchStrings.Count == 0 ? plan.Topic.Trim() : "");
    }

    internal void UseProposal(string text)
    {
        if (plan.FormalSearchStrings.Count >= MultivocalPlan.MaxSearchStrings) return;
        if (plan.FormalSearchStrings.Count == 0) plan.FormalSearchStrings.Add(plan.Topic.Trim());
        if (plan.FormalSearchStrings.Count < MultivocalPlan.MaxSearchStrings) plan.FormalSearchStrings.Add(text);
    }

    // Asks the model for search strings for the academic databases, with the systematic review's preview. Every
    // proposal is kept on the plan, so the protocol can say which the reviewer used and which not.
    internal async Task ProposeFormalStrings()
    {
        if (proposingStrings || proposalsLeft <= 0 || string.IsNullOrWhiteSpace(plan.Topic)) return;
        proposingStrings = true;
        proposalError = null;
        proposalsLeft--;
        try
        {
            var strings = await ReviewEngine.PreviewSearchStringsAsync(plan.Topic, plan.Goal, plan.InclusionCriteria, null);
            int before = plan.FormalStringsProposed.Count;
            foreach (var text in strings.Select(t => t.Trim()).Where(t => t.Length is > 0 and <= MultivocalPlan.MaxSearchStringLength))
                if (!plan.FormalStringsProposed.Contains(text, StringComparer.OrdinalIgnoreCase)) plan.FormalStringsProposed.Add(text);
            if (plan.FormalStringsProposed.Count == before)
                proposalError = "The model proposed nothing new. Write your own strings, or ask again.";
        }
        catch (Exception ex)
        {
            Logger.LogInformation("Formal search-string proposal failed: {Error}", ex.GetType().Name);
            proposalError = "The proposal did not work just now. Write your own strings, or try again.";
        }
        finally
        {
            proposingStrings = false;
        }
    }

    // The first thing missing in a step, worded for the link above the buttons; null when the step is complete.
    internal string? StepProblem(int index)
    {
        switch (index)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(plan.Topic)) return "Add the topic (step 1).";
                if (string.IsNullOrWhiteSpace(plan.Goal)) return "Add the goal (step 1).";
                if (plan.Audience == null) return "Choose who the review is for (step 1).";
                return null;
            case 1:
                return plan.IncludeGrey.Any(a => a == null) ? "Answer all seven questions on grey literature (step 2)." : null;
            case 2:
                if (plan.Questions.Any(q => string.IsNullOrWhiteSpace(q.Text))) return "Fill in or remove empty research questions (step 3).";
                if (plan.Questions.Any(q => MultivocalGuidelines.FindType(q.Type) == null)) return "Choose a type for every research question (step 3).";
                return null;
            case 3:
                if (plan.GreyTypes.Count == 0) return "Choose at least one kind of grey literature (step 4).";
                if (plan.GreySearches.Count == 0) return "Choose at least one place to search (step 4).";
                if (!plan.HasCriteria) return "Say what a source must be about to be included (step 4).";
                if (plan.QualityThreshold is < 1 or > MultivocalGuidelines.QualityPointsMax) return "Set the quality threshold between 1 and 20 points (step 4).";
                if (plan.StoppingRule == "effort" && plan.TopHits is < 10 or > 500) return "Look at between 10 and 500 top hits per search string (step 4).";
                return null;
            default:
                if (Problems.Count > 0) return Problems[0];
                return FlagsConfirmed ? null : "Confirm the text that reads like instructions (step 5).";
        }
    }

    internal (int Step, string Text)? FirstProblem()
    {
        for (int i = 0; i < Steps.Length; i++)
            if (StepProblem(i) is { } text) return (i, text);
        return null;
    }

    internal string SettingsLine()
    {
        int questions = plan.Numbered().Count;
        return $"{(plan.UsesFormalPool ? "Grey and academic" : "Grey only")} · {questions} research question{(questions == 1 ? "" : "s")} · {plan.GreySearches.Count} search{(plan.GreySearches.Count == 1 ? "" : "es")} · {StoppingSummary()} · threshold {plan.QualityThreshold}/{MultivocalGuidelines.QualityPointsMax}";
    }

    internal string StoppingSummary() => plan.StoppingRule switch
    {
        "effort" => $"top {plan.TopHits} hits",
        "saturation" => "until saturation",
        _ => "every hit",
    };

    internal async Task GoTo(int index)
    {
        step = Math.Clamp(index, 0, Steps.Length - 1);
        error = null;
        if (step == Steps.Length - 1) panelTab = "protocol";
        try { await JS.InvokeVoidAsync("traceableReview.focus", "step-title"); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            Logger.LogDebug("Could not move focus to the step title: {Message}", ex.Message);
        }
    }

    // Ticks or unticks one key, keeping the guidelines' order so the protocol lists them the same way every time.
    internal static List<string> Toggle(List<string> list, string key, ChangeEventArgs e, IEnumerable<string> order)
    {
        bool on = e.Value is bool b && b;
        var chosen = list.Where(k => k != key).Concat(on ? new[] { key } : Array.Empty<string>()).ToHashSet();
        return order.Where(chosen.Contains).ToList();
    }

    // Fills every step with a complete example (preview only), so the form and the protocol can be tried quickly.
    internal void FillExample()
    {
        plan = MultivocalPlan.Example(plan.Card);
        if (!plan.IsGreyOnly)
        {
            plan.FormalDatabases = MultivocalPlan.ExampleFormalDatabases.Where(FormalAvailable).ToList();
            plan.FormalSearchStrings = new List<string> { "context engineering large language model agents", "LLM agent memory management" };
        }
        error = null;
    }

    internal static string AudienceLabel(ReviewAudience? audience) => audience switch
    {
        ReviewAudience.Researchers => "Researchers",
        ReviewAudience.Practitioners => "Practitioners",
        ReviewAudience.Both => "Both",
        _ => "not set",
    };

    internal async Task WriteProtocolAsync()
    {
        if (saving) return;
        saving = true;
        error = null;
        try
        {
            var (newRunId, newEditKey) = await Planner.CreateAsync(plan, FlagsConfirmed ? FlagLines : null);
            // The protocol is written; the rest runs on the server, one step after another, without a click.
            Runner.Start(newRunId, newEditKey, PrerenderClientAddress);
            // Only this browser keeps the run's edit key, as for a systematic review.
            try { await JS.InvokeVoidAsync("traceableReview.saveEditKey", newRunId.ToString(), newEditKey); }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
            {
                // The run is written either way; without the key this browser cannot change it later, as for a systematic review.
                Logger.LogDebug("Could not keep the edit key in this browser: {Message}", ex.Message);
            }
            Nav.NavigateTo($"/mlr/{newRunId}");
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
        }
        finally
        {
            saving = false;
        }
    }
}
