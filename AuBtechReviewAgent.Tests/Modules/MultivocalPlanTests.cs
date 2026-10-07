using System.Security.Cryptography;
using System.Text;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>Planning a multivocal review (MLR block C): the plan, its protocol (G1) and starting a planned run.</summary>
public class MultivocalPlanTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "mlrplan-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFolders.TryDelete(_root);

    private static MultivocalPlan Complete(string card = "multivocal")
    {
        var plan = new MultivocalPlan
        {
            Card = card,
            Topic = "Context engineering for LLM agents",
            Goal = "Map how practitioners manage the context window of LLM agents.",
            ExistingReviews = "One survey from 2025 covers prompting, not context management.",
            Audience = ReviewAudience.Both,
            IncludeGrey = new List<bool?> { true, false, true, false, false, true, true },
            Questions = new List<PlannedQuestion>
            {
                new() { Text = "Which context engineering practices do practitioners report?", Type = "description" },
                new() { Text = "Which tools support them?", Type = "existence", Parent = 0 },
                new() { Text = "Do these practices reduce agent failures?", Type = "causality" },
            },
            QualityThreshold = 12,
            InclusionCriteria = "Sources on context engineering practice for LLM agents.",
            ExclusionCriteria = "Marketing pages.",
            TopHits = 50,
        };
        return plan;
    }

    [Fact]
    public void TheProtocolContainsEveryPlanningAnswer()
    {
        var plan = Complete();
        Assert.Empty(plan.Problems());

        string protocol = MultivocalProtocol.Write(plan, Guid.NewGuid(), new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("Review type: Multivocal Literature Review, following Garousi", protocol);
        Assert.Contains(plan.Topic, protocol);
        Assert.Contains(plan.Goal, protocol);
        Assert.Contains(plan.ExistingReviews, protocol);
        Assert.Contains("Intended audience: researchers and practitioners", protocol);
        for (int i = 0; i < MultivocalGuidelines.IncludeGreyQuestions.Count; i++)
            Assert.Contains($"{i + 1}. {MultivocalGuidelines.IncludeGreyQuestions[i]} {(plan.IncludeGrey[i] == true ? "Yes" : "No")}", protocol);
        Assert.Contains("4 of 7 answers are \"yes\"", protocol);
        Assert.Contains("- RQ1: Which context engineering practices do practitioners report? (type: Description and classification)", protocol);
        Assert.Contains("  - RQ1.1: Which tools support them? (type: Existence)", protocol);
        Assert.Contains("- RQ2: Do these practices reduce agent failures? (type: Causality; sources may not allow this type to be answered)", protocol);
        foreach (var key in plan.GreyTypes) Assert.Contains(MultivocalGuidelines.GreyTypes.First(t => t.Key == key).Label, protocol);
        foreach (var key in plan.GreySearches) Assert.Contains(MultivocalGuidelines.GreySearches.First(s => s.Key == key).Label, protocol);
        Assert.Contains("the top 50 hits of each search string", protocol);
        Assert.Contains("sources scoring below 12 points are excluded", protocol);
        Assert.Contains("No general web search engine is used", protocol);
        Assert.Contains("Practitioners and authors are not contacted", protocol);
        Assert.Contains("Talks and videos are not covered.", protocol);
        Assert.Contains("Formal literature:", protocol);
        Assert.Contains("Inclusion criteria (G9): Sources on context engineering practice for LLM agents.", protocol);
        Assert.Contains("Exclusion criteria (G9): Marketing pages.", protocol);
        Assert.Contains("screened twice", protocol);

        plan.InclusionCriteria = " ";
        Assert.Contains("Say what a source must be about to be included (the inclusion criteria).", plan.Problems());
    }

    [Fact]
    public void TranscriptsAreStatedWhenTalksAreChosenAndADroppedSearchIsNamedNotFatal()
    {
        var plan = Complete();
        plan.GreyTypes.Add("talks");
        plan.GreySearches.Add("brave"); // a plan made before Brave was dropped

        string protocol = MultivocalProtocol.Write(plan, Guid.NewGuid(), DateTime.UtcNow);

        Assert.Contains("only when its transcript is published", protocol);
        Assert.Contains("No general web search engine is used", protocol);
        Assert.Contains("- brave", protocol);
        Assert.Contains(plan.Problems(), p => p.Contains("search", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("multivocal")]
    [InlineData("grey")]
    public void TheExampleFillsEveryStepCompletely(string card)
    {
        var plan = MultivocalPlan.Example(card);

        Assert.Empty(plan.Problems());
        Assert.Equal(card, plan.Card);
        Assert.All(plan.IncludeGrey, a => Assert.NotNull(a));
        Assert.Equal(new[] { "RQ1", "RQ1.1", "RQ1.2", "RQ2", "RQ3" }, plan.Numbered().Select(q => q.Number));
        Assert.Contains("Context engineering for LLM agents", MultivocalProtocol.Write(plan, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void AnUnansweredGreyLiteratureQuestionIsRefused()
    {
        var plan = Complete();
        plan.IncludeGrey[3] = null;
        Assert.Contains("Answer all seven questions on including grey literature.", plan.Problems());

        plan.IncludeGrey = MultivocalGuidelines.IncludeGreyQuestions.Select(_ => (bool?)null).ToList();
        Assert.Contains("Answer all seven questions on including grey literature.", plan.Problems());
    }

    [Fact]
    public void AllNoIsAllowedButTheProtocolSaysASystematicReviewMayBeEnough()
    {
        var plan = Complete();
        plan.IncludeGrey = MultivocalGuidelines.IncludeGreyQuestions.Select(_ => (bool?)false).ToList();

        Assert.Empty(plan.Problems());
        Assert.False(plan.GreyLiteratureIndicated);
        Assert.Contains("a systematic review may be enough", MultivocalProtocol.Write(plan, Guid.NewGuid(), DateTime.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void TheQualityThresholdIsInPointsOutOfTwenty(int threshold)
    {
        var plan = Complete();
        plan.QualityThreshold = threshold;
        Assert.Contains("Set the quality threshold between 1 and 20 points.", plan.Problems());
    }

    [Fact]
    public void EveryQuestionNeedsATypeAndText()
    {
        var plan = Complete();
        plan.Questions[2].Type = "";
        plan.Questions[1].Text = " ";
        plan.Normalized();
        var problems = plan.Problems();
        Assert.Contains("Choose a type for every research question.", problems);
        Assert.Contains("Write at least one research question, and fill in or remove empty ones.", problems);
    }

    [Fact]
    public void SubQuestionsStayWithTheirMainQuestion()
    {
        var plan = new MultivocalPlan();             // one empty main question
        plan.AddQuestion(null);                       // RQ2
        plan.AddQuestion(0);                          // RQ1.1, inserted after RQ1
        plan.AddQuestion(0);                          // RQ1.2
        plan.AddQuestion(3);                          // RQ2.1 (RQ2 is now at index 3)
        Assert.Equal(new[] { "RQ1", "RQ1.1", "RQ1.2", "RQ2", "RQ2.1" }, plan.Numbered().Select(q => q.Number));

        plan.RemoveQuestion(0);                       // RQ1 and its sub-questions go
        Assert.Equal(new[] { "RQ1", "RQ1.1" }, plan.Numbered().Select(q => q.Number));
        Assert.Equal(0, plan.Questions[1].Parent);
    }

    [Fact]
    public async Task APlannedRunIsOnFileBeforeAnySearch()
    {
        var store = new RunStore(_root);
        var planner = new MultivocalPlanner(store);

        var (runId, editKey) = await planner.CreateAsync(Complete("grey"));

        var header = store.LoadHeader(runId)!;
        Assert.Equal("multivocal", header.Module);
        Assert.Equal("grey", header.Card);
        Assert.Equal(MultivocalPlanner.StagePlanned, header.Stage);
        Assert.True(store.CanEdit(runId, editKey));

        string protocol = store.ReadProtocol(runId)!;
        Assert.Contains("Review type: Grey Literature Review", protocol);
        Assert.Contains("grey literature only", protocol);
        Assert.DoesNotContain("Formal literature:", protocol);

        var planned = planner.Load(runId)!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(protocol))).ToLowerInvariant(), planned.ProtocolSha256);
        Assert.Equal(ReviewAudience.Both, planned.Plan.Audience);
        Assert.Equal("/review/" + runId, ReviewModules.ElsewhereFor(new RunHeader(runId, "systematic", "systematic", DateTime.UtcNow), "multivocal"));
        Assert.Null(ReviewModules.ElsewhereFor(header, "multivocal"));
    }

    [Fact]
    public async Task AnIncompletePlanWritesNothing()
    {
        var store = new RunStore(_root);
        var plan = Complete();
        plan.Topic = "";

        await Assert.ThrowsAsync<ArgumentException>(() => new MultivocalPlanner(store).CreateAsync(plan));
        Assert.False(Directory.Exists(_root) && Directory.EnumerateDirectories(_root).Any());
    }

    [Fact]
    public async Task ThePlanIsInTheArchive()
    {
        var store = new RunStore(_root);
        var (runId, _) = await new MultivocalPlanner(store).CreateAsync(Complete());

        var zip = RunArchive.Build(runId, store.FolderOf(runId), Array.Empty<(string, byte[])>(), ReviewModules.Find("multivocal")!.ArchiveFiles, null, null);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(MultivocalPlanner.PlanFile, names);
        Assert.Contains(RunStore.ProtocolFile, names);
        Assert.DoesNotContain(RunStore.OwnerFile, names);
    }
}
