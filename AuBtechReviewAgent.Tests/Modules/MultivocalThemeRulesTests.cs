using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The rules of a multivocal synthesis (MLR block F, G13) that are decided in code: what a theme rests on, how values
/// are grouped, the checks on the model's themes and placements, tensions, the second placement's agreement, and one
/// research question synthesised with a fake model.
/// </summary>
public class MultivocalThemeRulesTests
{
    private static SynthesisFinding Finding(string id, string address, string kind, double points) => new()
    {
        Id = id, Address = address, Kind = kind, Tier = MultivocalSynthesiser.TierOf(kind), QualityPoints = points, Question = "RQ1",
    };

    [Fact]
    public void WhatAThemeRestsOnIsCountedInCode()
    {
        var findings = new List<SynthesisFinding>
        {
            Finding("F1.1", "a/1", "blogs", 11), Finding("F1.2", "a/1", "blogs", 11),
            Finding("F2.1", "b/2", "code", 13), Finding("F3.1", "c/3", "white-papers", 18),
        };

        var thirdTierOnly = MultivocalSynthesiser.Count(new SynthesisTheme { Findings = { "F1.1", "F1.2", "F2.1" } }, findings);
        Assert.Equal(2, thirdTierOnly.Sources); // two findings of one source count once
        Assert.Equal(2, thirdTierOnly.ThirdTier);
        Assert.Equal(12, thirdTierOnly.MeanQuality);
        Assert.Equal("3rd-tier grey literature only", thirdTierOnly.EvidenceLabel);

        var mixed = MultivocalSynthesiser.Count(new SynthesisTheme { Findings = { "F1.1", "F3.1" } }, findings);
        Assert.Equal((1, 0, 1), (mixed.FirstTier, mixed.SecondTier, mixed.ThirdTier));
        Assert.Equal("grey literature only", mixed.EvidenceLabel);

        findings[3].Literature = "formal";
        Assert.Equal("grey and formal literature", MultivocalSynthesiser.Count(new SynthesisTheme { Findings = { "F1.1", "F3.1" } }, findings).EvidenceLabel);
        Assert.Equal((1, 2, 3), (MultivocalSynthesiser.TierOf("theses"), MultivocalSynthesiser.TierOf("qa"), MultivocalSynthesiser.TierOf("blogs")));
    }

    private static SynthesisFinding Coded(string id, string address, string attribute, string value) => new()
    {
        Id = id, Address = address, Attribute = attribute, Value = value, Quote = "Quote " + id, Title = "Page " + address,
        Kind = "blogs", Tier = 3, Question = "RQ1",
    };

    [Fact]
    public void FindingsAreGroupedByAttributeAndValueInCode()
    {
        var findings = new List<SynthesisFinding>
        {
            Coded("F1.1", "a", "Practice", "Retrieval"), Coded("F2.1", "b", "Practice", " retrieval "),
            Coded("F2.2", "b", "Practice", "Retrieval"), Coded("F3.1", "c", "Practice", "Summarisation"),
            Coded("F3.2", "c", "Tool", "Retrieval"),
        };

        var groups = MultivocalSynthesiser.ValueGroups(findings);

        Assert.Equal(new[] { "V1", "V2", "V3" }, groups.Select(g => g.Id));
        var retrieval = groups[0];
        Assert.Equal(("Practice", "Retrieval"), (retrieval.Attribute, retrieval.Value)); // case and spaces do not split a value
        Assert.Equal(new[] { "F1.1", "F2.1", "F2.2" }, retrieval.Findings);
        Assert.Equal(2, retrieval.Sources);
        Assert.Equal(2, retrieval.Samples.Count); // one sample quote per source
        Assert.Equal("Practice: Retrieval (3 findings)", retrieval.Label);
        Assert.Equal("Tool", groups[2].Attribute); // the same value under another attribute is its own group
    }

    [Fact]
    public void TheThemesAndThePlacementAreCheckedInCode()
    {
        ModelThemeList Themes(params string[] names) => new() { Themes = names.Select(n => new ModelThemeName { Name = n, Description = "d" }).ToList() };
        Assert.Null(ModelThemeList.Problem(Themes("Summaries", "Retrieval")));
        Assert.Contains("one to 6 themes", ModelThemeList.Problem(Themes()));
        Assert.Contains("one to 6 themes", ModelThemeList.Problem(Themes("a", "b", "c", "d", "e", "f", "g")));
        Assert.Contains("needs a name", ModelThemeList.Problem(Themes("a", " ")));
        Assert.Contains("two themes are called", ModelThemeList.Problem(Themes("Summaries", "summaries ")));

        // How many sources say something is counted in code (seen in a live run: "The majority of sources report…").
        ModelThemeList Described(string description) => new() { Themes = { new ModelThemeName { Name = "Retrieval", Description = description } } };
        Assert.Null(ModelThemeList.Problem(Described("Retrieval brings documents into the context when the agent needs them.")));
        Assert.Contains("says how many sources", ModelThemeList.Problem(Described("The majority of sources report fewer failures.")));
        Assert.Contains("says how many sources", ModelThemeList.Problem(Described("Most practitioners keep a summary.")));
        Assert.Contains("says how many sources", ModelThemeList.Problem(Described("12 sources describe it.")));

        var groups = new HashSet<string> { "V1", "V2" };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summaries", "Retrieval" };
        ModelPlacement Placed(params ModelPlaced[] p) => new() { Placements = p.ToList() };
        ModelPlaced In(string group, params string[] themes) => new() { Group = group, Themes = themes.ToList() };

        Assert.Null(ModelPlacement.Problem(Placed(In("V1", "summaries"), In("V2", "Retrieval", "Summaries")), groups, names));
        Assert.Contains("\"V9\" is not one of the values", ModelPlacement.Problem(Placed(In("V1", "Summaries"), In("V2", "Retrieval"), In("V9", "Retrieval")), groups, names));
        Assert.Contains("values V2 are not placed", ModelPlacement.Problem(Placed(In("V1", "Summaries")), groups, names));
        Assert.Contains("\"Memory\" is not one of the themes", ModelPlacement.Problem(Placed(In("V1", "Memory"), In("V2", "Retrieval")), groups, names));
        Assert.Contains("needs the reason", ModelPlacement.Problem(Placed(In("V1", "Summaries"), In("V2")), groups, names));
        var withReason = Placed(In("V1", "Summaries"), In("V2"));
        withReason.Placements[1].Reason = "Off the question.";
        Assert.Null(ModelPlacement.Problem(withReason, groups, names));
    }

    [Fact]
    public void ATensionIsKeptOnlyBetweenTwoSourcesOfTheTheme()
    {
        var findings = new List<SynthesisFinding>
        {
            Coded("F1.1", "a", "Practice", "Summarise"), Coded("F1.2", "a", "Practice", "Never summarise"),
            Coded("F2.1", "b", "Practice", "Never summarise"),
        };
        var groups = MultivocalSynthesiser.ValueGroups(findings);
        var answer = new ModelTensions
        {
            Tensions =
            {
                new ModelTension { GroupA = "V1", GroupB = "V2", FindingA = "F1.1", FindingB = "F2.1", Description = "V1 keeps a summary, V2 never does, unlike V9." },
                new ModelTension { GroupA = "V1", GroupB = "V7", Description = "Outside the theme." },
                new ModelTension { GroupA = "V1", GroupB = "V1", Description = "With itself." },
            },
        };

        var tensions = MultivocalSynthesiser.TensionsFor(answer, groups, findings, out var notes);

        var kept = Assert.Single(tensions);
        Assert.Equal(("F1.1", "F2.1"), (kept.FindingA, kept.FindingB)); // the two quotes the model named
        // The reader sees the values' names, never the ids the model was given.
        Assert.Equal("Summarise against Never summarise. \"Summarise\" keeps a summary, \"Never summarise\" never does, unlike another value.", kept.Description);
        Assert.Equal("2 tension(s) named a value outside the theme; left out.", Assert.Single(notes));

        var oneSource = MultivocalSynthesiser.TensionsFor(answer, MultivocalSynthesiser.ValueGroups(findings.Take(2).ToList()), findings.Take(2).ToList(), out var oneNotes);
        Assert.Empty(oneSource);
        Assert.Equal(new[] { "2 tension(s) named a value outside the theme; left out.", "1 tension(s) were between values of one source only; left out." }, oneNotes);
    }

    [Fact]
    public void ATensionWithoutTwoCheckedQuotesCitesNoSourceAndHasNoPoolLabel()
    {
        // Issue #463: code used to pick the first pair of findings of the two values, which need not disagree.
        static SynthesisFinding In(string pool, SynthesisFinding finding) { finding.Literature = pool; return finding; }
        var findings = new List<SynthesisFinding>
        {
            In("grey", Coded("F1.1", "a", "Practice", "Summarise")), In("formal", Coded("F2.1", "b", "Practice", "Never summarise")),
            In("grey", Coded("F3.1", "c", "Practice", "Never summarise")),
        };
        var groups = MultivocalSynthesiser.ValueGroups(findings);
        ModelTensions Named(string? a, string? b) => new() { Tensions = { new ModelTension { GroupA = "V1", GroupB = "V2", FindingA = a, FindingB = b, Description = "V1 keeps a summary, V2 never does." } } };

        foreach (var (a, b) in new (string?, string?)[] { (null, null), ("", ""), ("F1.1", "F9.9"), ("F2.1", "F1.1"), ("F1.1", null) })
        {
            var tension = Assert.Single(MultivocalSynthesiser.TensionsFor(Named(a, b), groups, findings, out var notes));
            Assert.Equal(("", "", null), (tension.FindingA, tension.FindingB, tension.Between));
            Assert.Equal(a is null or "" ? 0 : 1, notes.Count(n => n.Contains("not cited")));
        }

        var checkedPair = Assert.Single(MultivocalSynthesiser.TensionsFor(Named("F1.1", "F2.1"), groups, findings, out _));
        Assert.Equal(("F1.1", "F2.1", SynthesisTension.ArtAgainstPractice), (checkedPair.FindingA, checkedPair.FindingB, checkedPair.Between));
        var withinPractice = Assert.Single(MultivocalSynthesiser.TensionsFor(Named("F1.1", "F3.1"), groups, findings, out _));
        Assert.Equal("within the state of the practice", withinPractice.Between);
    }

    [Fact]
    public void TheSecondPlacementsAgreementIsCountedOverEveryValueAndTheme()
    {
        var themes = new[] { "Summaries", "Retrieval" };
        HashSet<string> In(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
        var first = new Dictionary<string, HashSet<string>> { ["V1"] = In("Summaries"), ["V2"] = In("Retrieval"), ["V3"] = In("Summaries", "Retrieval"), ["V4"] = In() };
        var second = new Dictionary<string, HashSet<string>> { ["V1"] = In("summaries"), ["V2"] = In("Summaries"), ["V3"] = In("Summaries", "Retrieval") }; // V4 not placed the second time

        var agreement = MultivocalSynthesiser.ComparePlacements("RQ1", themes, first, second);

        Assert.Equal((6, 4), (agreement.CellsCompared, agreement.Agreements)); // V4 is left out of the comparison
        Assert.Equal(new[] { "V2, \"Summaries\": second placement only", "V2, \"Retrieval\": first placement only" }, agreement.Disagreements);
        Assert.Equal(0.25, agreement.Kappa!.Value); // 4 of 6 cells agree, 4 of 6 are "in the theme" both times: (4/6 - 20/36) / (1 - 20/36)
    }

    [Fact]
    public void ThemesHoldingTheSameValuesAreMerged()
    {
        var merged = MultivocalSynthesiser.MergeIdenticalThemes(new List<(string, List<string>)>
        {
            ("Retrieval improves reliability", new() { "V1", "V2" }),
            ("Conditional benefits", new() { "V3" }),
            ("Quality of retrieved context", new() { "V2", "V1" }), // the same values in another order
            ("Evaluation differences", new() { "V3" }),
            ("Empty", new()),
            ("Also empty", new()),
        });

        Assert.Equal(new[] { ("Retrieval improves reliability", "Quality of retrieved context"), ("Conditional benefits", "Evaluation differences") }, merged);
    }

    [Fact]
    public void NotStatedAndOtherValuesAreDecidedInCode()
    {
        Assert.True(MultivocalSynthesiser.IsNotStated("Not addressed."));
        Assert.True(MultivocalSynthesiser.IsNotStated(" not stated"));
        Assert.False(MultivocalSynthesiser.IsNotStated("Retrieval"));
        Assert.True(MultivocalSynthesiser.IsOther(new ValueGroup { Value = "other" }));
        Assert.False(MultivocalSynthesiser.IsOther(new ValueGroup { Value = "Other tools" }));
    }

    [Fact]
    public async Task NamesOfAnOpenAttributeAreNeverAskedAboutTensions()
    {
        var findings = new List<SynthesisFinding> { Coded("F1.1", "a", "Tool", "MemGPT"), Coded("F2.1", "b", "Tool", "Letta") };
        var model = new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Name one to") ? """{"themes":[{"name":"Memory tools","description":"Tools that keep agent memory."}]}"""
            : prompt.Contains("VALUES TO PLACE") ? """{"placements":[{"group":"V1","themes":["Memory tools"],"reason":""},{"group":"V2","themes":["Memory tools"],"reason":""}]}"""
            : throw new InvalidOperationException("Tensions were asked for tool names."));
        var file = new MultivocalSynthesisFile();

        await MultivocalSynthesiser.SynthesiseQuestionAsync(model, "topic", "RQ1.1", "Which tools?", MultivocalSynthesiser.ValueGroups(findings), findings,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tool" }, file, null);

        Assert.Equal(3, model.Prompts.Count); // themes, placement, second placement; no tensions
        Assert.Empty(Assert.Single(file.Themes).Tensions);
        var agreement = Assert.Single(file.SecondPlacements);
        Assert.Equal((2, 2, 1.0), (agreement.CellsCompared, agreement.Agreements, agreement.Kappa!.Value));
    }

    [Fact]
    public async Task ABatchTheModelCannotPlaceIsKeptAsNotPlacedAndTheRestGoOn()
    {
        // 31 values: the first batch of 30 never passes the check, the last one is placed.
        var findings = Enumerable.Range(1, 31).Select(i => Coded($"F{i}.1", $"s{i}", "Practice", $"Value {i}")).ToList();
        var groups = MultivocalSynthesiser.ValueGroups(findings);
        var model = new FakeChatService().RespondsWith(prompt =>
            prompt.Contains("Name one to") ? """{"themes":[{"name":"Context","description":"How sources keep context."},{"name":"Unused","description":"Nothing."}]}"""
            : prompt.Contains("V31 Practice") ? """{"placements":[{"group":"V31","themes":["Context"],"reason":""}]}"""
            : prompt.Contains("VALUES TO PLACE") ? """{"placements":[{"group":"V1","themes":["Context"],"reason":""}]}"""
            : """{"tensions":[]}""");
        var file = new MultivocalSynthesisFile();

        await MultivocalSynthesiser.SynthesiseQuestionAsync(model, "topic", "RQ1", "How?", groups, findings, new HashSet<string>(), file, null);

        Assert.Equal(30, file.Unassigned.Count);
        Assert.All(file.Unassigned, u => Assert.StartsWith("Not placed", u.Reason));
        Assert.Contains(file.Notes, n => n.Contains("30 value(s) could not be placed"));
        Assert.Contains(file.Notes, n => n.Contains("\"Unused\" got no values"));
        var theme = Assert.Single(file.Themes);
        Assert.Equal(new[] { "F31.1" }, theme.Findings);
    }
}
