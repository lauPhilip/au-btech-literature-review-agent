using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Issue #466: in the real run 50e9a8e3 the table of tools counted lists, models, languages, methods and a web address as
/// one-off tools, and MCP appeared under three names. The values below are the run's own.
/// </summary>
public class ToolNameRulesTests
{
    private const string Tools = "Tool or framework";

    [Fact]
    public void AListIsSplitIntoItsNames()
    {
        Assert.Equal(new[] { "Claude Code", "OpenCode", "Amp", "Kiro", "Codex", "Gemini CLI" }, ToolNameRules.Split("Claude Code, OpenCode, Amp, Kiro, Codex, Gemini CLI"));
        Assert.Equal(new[] { "LangChain", "LangGraph" }, ToolNameRules.Split("LangChain / LangGraph"));
        Assert.Equal(new[] { "NoneBot 2", "OneBot V11" }, ToolNameRules.Split("NoneBot 2 + OneBot V11"));
        Assert.Equal(new[] { "GPT-4.1", "o4-mini" }, ToolNameRules.Split("GPT-4.1 and o4-mini"));
        Assert.Equal(new[] { "Retrieval-Augmented Generation (RAG)", "MemGPT", "LangChain", "LlamaIndex", "ReAct", "A-MEM", "HiAgent", "MCP-based systems" },
            ToolNameRules.Split("Retrieval-Augmented Generation (RAG), MemGPT, LangChain, LlamaIndex, ReAct, A-MEM, HiAgent, and MCP-based systems"));
    }

    [Theory]
    [InlineData("Claude 5 models (Opus 5, Fable 5)")] // commas inside brackets
    [InlineData("SSCAR (Session State Continuity & Anchor Replay Family)")]
    [InlineData("AgentMemoryWorld/Awesome-Agent-Memory")] // a repository, not "A / B"
    [InlineData("bonsai-memory (OpenClaw skill), works with any LLM agent framework using persistent memory files")] // a phrase, not a list
    [InlineData("Claude Code")]
    public void ANameOrAPhraseStaysWhole(string value) => Assert.Equal(new[] { value }, ToolNameRules.Split(value));

    [Theory]
    [InlineData("MCP (Model Context Protocol) server", "MCP")]
    [InlineData("Model Context Protocol (MCP)-based toolkit", "Model Context Protocol")]
    [InlineData("MCP-based systems", "MCP")]
    [InlineData("MCP (Model Context Protocol)", "Model Context Protocol (MCP)")]
    public void ASpellingWithAGenericWordMatchesTheTool(string a, string b) =>
        Assert.True(OpenNameMerger.Keys(a).Intersect(OpenNameMerger.Keys(b)).Any());

    [Theory]
    [InlineData("Playwright MCP server", "MCP")]
    [InlineData("Claude Code SDK", "Claude Code")]
    [InlineData("LangChain Deep Agents", "LangChain")]
    public void AToolThatContainsAnotherToolsNameStaysApart(string a, string b) =>
        Assert.False(OpenNameMerger.Keys(a).Intersect(OpenNameMerger.Keys(b)).Any());

    [Theory]
    [InlineData("GPT-4", ToolNameRules.LanguageModel)]
    [InlineData("Llama 3.2", ToolNameRules.LanguageModel)]
    [InlineData("Whisper-v3", ToolNameRules.LanguageModel)]
    [InlineData("LLaMa3.2-Vision", ToolNameRules.LanguageModel)]
    [InlineData("Claude (Sonnet)", ToolNameRules.LanguageModel)]
    [InlineData("Anthropic Claude", ToolNameRules.LanguageModel)]
    [InlineData("OpenAI GPT models", ToolNameRules.LanguageModel)]
    [InlineData("grok-4-1-fast-reasoning (Grok)", ToolNameRules.LanguageModel)]
    [InlineData("Gemini Embedding (gemini-embedding-001)", ToolNameRules.LanguageModel)]
    [InlineData("Python", ToolNameRules.ProgrammingLanguage)]
    [InlineData("ReAct", ToolNameRules.Method)]
    [InlineData("Retrieval-Augmented Generation (RAG)", ToolNameRules.Method)]
    [InlineData("Monte Carlo Tree Search (MCTS)", ToolNameRules.Method)]
    [InlineData("Local Large Language Models (LLMs)", ToolNameRules.GeneralTerm)]
    [InlineData("https://github.com/ShanglinWu/LLM-MAS_Memory_Survey.git", ToolNameRules.WebAddress)]
    public void WhatIsNotATool(string name, string reason) => Assert.Equal(reason, ToolNameRules.NotATool(name));

    [Theory]
    [InlineData("Claude Code")]
    [InlineData("Gemini CLI")]
    [InlineData("Claude Developer Platform")]
    [InlineData("Google ADK")]
    [InlineData("OpenAI Swarm")]
    [InlineData("Docker")]
    [InlineData("LangGraph")]
    [InlineData("MCP")]
    public void ToolsAreCounted(string name) => Assert.Null(ToolNameRules.NotATool(name));

    [Fact]
    public void OnlyAnAttributeAboutToolsLeavesModelsOut()
    {
        Assert.True(ToolNameRules.IsToolAttribute(new MapAttribute { Name = Tools, Open = true, Description = "Specific tool, framework, or platform named as supporting context engineering practices." }));
        Assert.False(ToolNameRules.IsToolAttribute(new MapAttribute { Name = "Model used", Open = true, Description = "The language model the tool runs on." }));
        Assert.False(ToolNameRules.IsToolAttribute(new MapAttribute { Name = Tools, Open = false }));
    }

    private static MultivocalExtractionFile Run(params (string Title, string[] Values)[] sources)
    {
        var map = new MapVersion
        {
            Number = 1,
            Attributes = { new MapAttribute { Name = Tools, Question = "RQ1.1", Open = true, Multiple = true, Description = "Specific tool, framework, or platform." } },
        };
        var file = new MultivocalExtractionFile();
        foreach (var (title, values) in sources)
            file.Sources.Add(new GreyExtraction
            {
                Address = title, Title = title,
                Attributes = { new ExtractedAttribute { Attribute = Tools, Values = values.Select(v => new ExtractedValue { Value = v, Quote = "q " + v }).ToList() } },
            });
        OpenNameMerger.Apply(file, map, MultivocalPlan.Example());
        return file;
    }

    [Fact]
    public void TheCountsAreSplitMergedAndFilteredAndEveryStepIsOnFile()
    {
        var file = Run(
            ("Post one", new[] { "Claude Code, OpenCode, Amp, Kiro, Codex, Gemini CLI" }),
            ("Post two", new[] { "n8n, PostgreSQL, Claude (Sonnet), MCP (Model Context Protocol), Telegram" }),
            ("Post three", new[] { "MCP (Model Context Protocol) server", "Codex", "GPT-4" }),
            ("Post four", new[] { "Model Context Protocol (MCP)-based toolkit", "Python", "Codex" }));

        var counts = file.Counts(Tools).ToDictionary(c => c.Value, c => c.Sources);
        Assert.Equal(3, counts.Single(c => c.Key.Contains("MCP")).Value); // posts two, three and four
        Assert.Equal(3, counts["Codex"]);
        Assert.DoesNotContain(counts.Keys, k => k.Contains(','));
        Assert.DoesNotContain("GPT-4", counts.Keys);
        Assert.DoesNotContain("Python", counts.Keys);
        Assert.DoesNotContain("Claude (Sonnet)", counts.Keys);

        Assert.Equal(ToolNameRules.Version, file.NameRules);
        Assert.Equal(2, file.SplitValues.Count);
        Assert.Equal(new[] { ("Claude (Sonnet)", ToolNameRules.LanguageModel), ("GPT-4", ToolNameRules.LanguageModel), ("Python", ToolNameRules.ProgrammingLanguage) },
            file.NotToolValues.Select(n => (n.Value, n.Reason)).OrderBy(x => x.Value, StringComparer.Ordinal));
        // Each name split from a value keeps the value's quote, and a value left out stays on file in its source.
        Assert.All(file.Sources[0].Attributes[0].Values, v => Assert.Equal("q Claude Code, OpenCode, Amp, Kiro, Codex, Gemini CLI", v.Quote));
        Assert.Equal("GPT-4", Assert.Single(file.Sources[2].Attributes[0].NotATool).Value);

        string summary = OpenNameMerger.Summary(file)!;
        Assert.Contains("Values that named several things, split in code: \"Claude Code, OpenCode, Amp, Kiro, Codex, Gemini CLI\" into \"Claude Code\", \"OpenCode\"", summary);
        Assert.Contains("Not counted as tools, by the rules in code: language models \"Claude (Sonnet)\", \"GPT-4\"; programming languages \"Python\".", summary);
    }

    [Fact]
    public void ApplyingTheRulesTwiceChangesNothing()
    {
        var file = Run(("Post one", new[] { "LangChain / LangGraph", "Python" }), ("Post two", new[] { "LangGraph" }));
        var first = file.Counts(Tools).ToList();
        file.SplitValues.Clear(); file.NotToolValues.Clear(); file.NameMerges.Clear();
        OpenNameMerger.Apply(file, new MapVersion { Attributes = { new MapAttribute { Name = Tools, Question = "RQ1.1", Open = true, Multiple = true, Description = "tool" } } }, MultivocalPlan.Example());
        Assert.Equal(first, file.Counts(Tools).ToList());
    }

    [Fact]
    public void TheMethodStatesTheRulesAndWhatTheyDid()
    {
        var input = MultivocalPaperTests.Input();
        input.Extraction.NameRules = ToolNameRules.Version;
        input.Extraction.SplitValues.Add(new SplitValue { Attribute = "Tool", Value = "A, B", Parts = { "A", "B" } });
        input.Extraction.NotToolValues.Add(new NotToolValue { Attribute = "Tool", Value = "GPT-4", Reason = ToolNameRules.LanguageModel });

        string md = MultivocalReporter.Write(input).ToMarkdown();

        Assert.Contains(ToolNameRules.Described, md);
        Assert.Contains("1 value was split, 0 merges made, 1 value not counted as a tool and 0 values naming one of the review's own sources not counted; the data appendix lists each.", md);
    }

    [Fact]
    public async Task TheExtractionPromptAsksForOneToolPerValue()
    {
        var chat = new FakeChatService().RespondsWith(_ => """{"purpose":{"value":"x","quote":"page"},"attributes":[]}""");
        var map = new MapVersion { Number = 1, Attributes = { new MapAttribute { Name = Tools, Question = "RQ1.1", Open = true, Multiple = true, Description = "tool" } } };
        var record = new GreyRecord("x:1", "A post", "", "https://example.org/a", "someone", "example.org", "blogs", null, new Dictionary<string, long>());

        await MultivocalExtractor.AskAsync(chat, MultivocalPlan.Example(), map, record, "page");

        Assert.Contains("give each name as its own value, never a list of names in one value", chat.Prompts.Single());
        Assert.Contains("not language models, programming languages or methods", chat.Prompts.Single());
        Assert.Equal("grey-extraction-v4", MultivocalExtractor.PromptVersion);
    }
}
