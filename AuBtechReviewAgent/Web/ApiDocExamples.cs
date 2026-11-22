using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// Every request and answer the API documentation page (/docs/api) shows, in one place. ApiDocsPageTests sends each
/// request to the real API with the fake model, and checks that each answer has exactly the property names and
/// nesting of the real one, so the page cannot drift from the API. The values are made up but realistic.
/// </summary>
public static class ApiDocExamples
{
    public const string RunId = "3f2c9a4e-8b1d-4c6f-9e27-5a0d1b7c4e83";
    public const string EditKey = "q7Rk2vXbN9mWc4TfLp8sHj3YdZa6Ue1GoKiEw5Bn0Mx";
    public const string Reason = "Wrong search words";

    /// <summary>The body of the first call: the two required fields and an objective.</summary>
    public const string StartBody = """
        {
          "searchWords": "AI agents error recovery",
          "inclusion": "Studies of how AI agents find and fix their own errors",
          "objective": "Map how AI agents recover from errors"
        }
        """;

    public static string StartCurl(string baseUrl) => $"""
        curl -X POST {baseUrl}/api/v1/reviews \
          -H "X-Mistral-Key: $MISTRAL_API_KEY" \
          -H "Content-Type: application/json" \
          -d '{Indent(StartBody, 2).TrimStart()}'
        """;

    public static string Started(string baseUrl) => $$"""
        {
          "id": "{{RunId}}",
          "editKey": "{{EditKey}}",
          "stage": "Queued",
          "links": {
            "status": "{{baseUrl}}/api/v1/reviews/{{RunId}}",
            "page": "{{baseUrl}}/review/{{RunId}}"
          }
        }
        """;

    public static string StatusCurl(string baseUrl) => $"curl {baseUrl}/api/v1/reviews/{RunId}";

    public static string Status(string baseUrl) => $$"""
        {
          "id": "{{RunId}}",
          "stage": "Complete",
          "step": 7,
          "steps": 7,
          "stepName": "Done",
          "percent": 100,
          "detail": "",
          "queuePosition": 0,
          "counts": { "identified": 48, "screened": 41, "excluded": 29, "included": 12 },
          "verdicts": { "checked": 37, "supported": 31, "partlySupported": 4, "notSupported": 1, "unverifiable": 1 },
          "failure": null,
          "startedUtc": "2026-11-22T09:14:03Z",
          "completedUtc": "2026-11-22T09:31:47Z",
          "links": {
            "status": "{{baseUrl}}/api/v1/reviews/{{RunId}}",
            "page": "{{baseUrl}}/review/{{RunId}}"
          },
          "downloads": {
            "archive": "{{baseUrl}}/api/workspace/{{RunId}}/archive",
            "bibTex": "{{baseUrl}}/api/workspace/{{RunId}}/references.bib",
            "ris": "{{baseUrl}}/api/workspace/{{RunId}}/references.ris",
            "screenedRis": "{{baseUrl}}/api/workspace/{{RunId}}/screened.ris",
            "protocol": "{{baseUrl}}/api/workspace/{{RunId}}/protocol.md"
          }
        }
        """;

    public static string ReportCurl(string baseUrl) => $"curl {baseUrl}/api/v1/reviews/{RunId}/report";

    /// <summary>A finished report, cut to one item in each list.</summary>
    public const string Report = $$"""
        {
          "id": "{{RunId}}",
          "title": "How AI Agents Recover from Errors: A Systematic Review",
          "abstract": "We review 12 studies on how AI agents find and fix their own errors.",
          "sections": [
            {
              "id": "s4",
              "heading": "4 Results and synthesis",
              "level": 1,
              "text": "Agents that check their own steps recover more often [3]."
            }
          ],
          "citations": [
            {
              "reference": 3,
              "section": "s4",
              "sentenceIndex": 0,
              "sentence": "Agents that check their own steps recover more often [3].",
              "verdict": "supported",
              "quote": "self-checking agents recovered in 71% of the runs",
              "quoteVerified": true,
              "evidenceBasis": "full text",
              "reason": "The paper reports the recovery rate for self-checking agents.",
              "sourceLink": "https://doi.org/10.1234/agents.2025.017"
            }
          ],
          "references": [
            {
              "number": 3,
              "citation": "Doe, J. (2025). Self-checking agents. Future Internet.",
              "doi": "10.1234/agents.2025.017",
              "link": "https://doi.org/10.1234/agents.2025.017"
            }
          ],
          "prisma": {
            "identified": 48,
            "identifiedBySource": [ { "source": "arXiv", "count": 30 } ],
            "identifiedViaCitations": 0,
            "duplicatesRemoved": 7,
            "outsideDateRange": 0,
            "cappedBeyondMax": 0,
            "screened": 41,
            "screeningErrors": 0,
            "excludedPeerReview": 0,
            "excludedAtScreening": 29,
            "excludedByReason": [ { "reason": "Not about AI agents", "count": 18 } ],
            "included": 12,
            "fullTextRetrieved": 9,
            "abstractOnly": 3
          },
          "appVersion": "1.6.0",
          "protocolFingerprint": "7ab158ac",
          "protocolSha256": "dbdaaefd98dd06ecc028ea6042b0a7c5e4b78b61e22884525110269505c12f8c",
          "completedUtc": "2026-11-22T09:31:47Z"
        }
        """;

    public static string CancelCurl(string baseUrl) => $"""
        curl -X DELETE "{baseUrl}/api/v1/reviews/{RunId}?reason=Wrong%20search%20words" \
          -H "X-Edit-Key: {EditKey}"
        """;

    public static string Cancelled(string baseUrl) => $$"""
        {
          "id": "{{RunId}}",
          "reason": "{{Reason}}",
          "links": {
            "status": "{{baseUrl}}/api/v1/reviews/{{RunId}}",
            "page": "{{baseUrl}}/review/{{RunId}}"
          }
        }
        """;

    /// <summary>The answer to a start without a key, with the API's own title and detail (ApiProblems).</summary>
    public static string Problem()
    {
        var problem = ApiProblems.MissingKey();
        return $$"""
            {
              "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
              "title": "{{Escape(problem.Title)}}",
              "status": {{problem.Status}},
              "detail": "{{Escape(problem.Detail)}}"
            }
            """;
    }

    /// <summary>The MCP server in a client's JSON configuration, such as Claude Code's .mcp.json.</summary>
    public static string McpConfig(string baseUrl) => $$"""
        {
          "mcpServers": {
            "traceableai": {
              "type": "http",
              "url": "{{baseUrl}}{{McpSetup.Path}}",
              "headers": { "X-Mistral-Key": "${MISTRAL_API_KEY}" }
            }
          }
        }
        """;

    public const string McpPrompt = "Use TraceableAI to run a literature review on how AI agents recover from errors. Tell me when it is done.";

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.Split('\n').Select((line, i) => i == 0 ? line : new string(' ', spaces) + line));
}
