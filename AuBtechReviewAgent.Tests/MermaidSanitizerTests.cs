using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

public class MermaidSanitizerTests
{
    // The diagram from a real run that Mermaid rejected with "Syntax error in text".
    public const string RealRunDiagram = """
        graph LR
            A[Agentic AI Frameworks] --> B[Fault-Tolerance]
            A --> C[Orchestration Loops]
            A --> D[Safety & Security]
            B --> B1[Cognitive MAS for Cloud Automation [1]]
            B --> B2[OS-Layer Recovery (+20%) [4]]
            C --> C1[Goal-Driven Agents [3]]
            D --> D3[Domain-Specific Plugins [6]]
        """;

    [Fact]
    public void QuotesLabelsSoCitationMarkersAndParenthesesAreAllowed()
    {
        string clean = MermaidSanitizer.Sanitize(RealRunDiagram);

        Assert.StartsWith("graph LR", clean);
        Assert.Contains("B --> B1[\"Cognitive MAS for Cloud Automation [1]\"]", clean);
        Assert.Contains("B --> B2[\"OS-Layer Recovery (+20%) [4]\"]", clean);
        Assert.Contains("A --> D[\"Safety & Security\"]", clean);
        Assert.Contains("A[\"Agentic AI Frameworks\"] --> B[\"Fault-Tolerance\"]", clean);
    }

    [Fact]
    public void QuotesEdgeLabelsAndEscapesQuotes()
    {
        string clean = MermaidSanitizer.Sanitize("flowchart TD\nA -->|retry (3x)| B{Is \"safe\"?}\nstyle A fill:#f9f");

        Assert.Contains("A -->|\"retry (3x)\"| B{\"Is #quot;safe#quot;?\"}", clean);
        Assert.Contains("style A fill:#f9f", clean);
    }

    [Fact]
    public void AlreadyQuotedLabelsAreNotDoubleQuoted()
    {
        string clean = MermaidSanitizer.Sanitize("graph LR\nA[\"Ready [1]\"] --> B");

        Assert.Contains("A[\"Ready [1]\"] --> B", clean);
    }

    [Fact]
    public void RemovesCodeFencesAndRejectsNonDiagrams()
    {
        Assert.StartsWith("graph TD", MermaidSanitizer.Sanitize("```mermaid\ngraph TD\nA-->B\n```"));
        Assert.Equal("", MermaidSanitizer.Sanitize("Here is a diagram of the findings."));
        Assert.Equal("", MermaidSanitizer.Sanitize(null));
    }

    [Fact]
    public void EndIsNotUsedAsANodeId()
    {
        Assert.Contains("end_[\"Done\"]", MermaidSanitizer.Sanitize("graph LR\nA[Start] --> end[Done]"));
    }
}
