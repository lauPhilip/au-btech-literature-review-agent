# 9. The API and the MCP server

Other tools can start a systematic review and follow it without the website. This page covers the REST API under `/api/v1`, how it keeps the caller's key out of every file, and the limits that apply to it. The code is in `AuBtechReviewAgent/Api/`.

## The switch

The API is off in production until Lau turns it on. `Program.cs` reads the switch as `builder.Configuration.GetValue("Api:Enabled", builder.Environment.IsDevelopment())`, so it is on in Development and off everywhere else unless `Api:Enabled` is `true` in the server's configuration. The default lives in code, not in `appsettings.json`. With the switch off, `Program.cs` neither registers the API's services nor maps its routes. `ApiEndpoints.HideReviewApi` then answers 404 for every address under `/api/v1` and `/mcp`, because another route that matches the shape of the path would otherwise answer a POST with 405.

## Starting a review and following it

| Call | Answer |
|---|---|
| `POST /api/v1/reviews` with the caller's key in `X-Mistral-Key` | 202 with the run id, the edit key, the stage and the addresses to follow |
| `GET /api/v1/reviews/{id}` | 200 with the stage, the step of seven, the progress, the counts and, when done, the verdicts and the downloads |
| `GET /api/v1/reviews/{id}/report` | 200 with the finished report as data; 409 while the review is not complete |
| `GET /openapi/v1.json` | The OpenAPI description of these three calls |

The body of a start holds the New review form's fields as JSON. Only `searchWords` and `inclusion` are required, as on the form.

| Field | What it is |
|---|---|
| `objective` | What the review should find out |
| `searchWords` | The search words, the primary query |
| `searchStrings` | Up to four extra search strings; left out, the model proposes them during the run |
| `inclusion`, `exclusion` | The eligibility criteria |
| `yearFrom`, `yearTo` | Publication years, 0 for no limit |
| `databases` | Keys from `SourceCatalog`: `arxiv`, `scopus`, `ieee`, `openalex`, `semanticscholar`, `crossref`; left out, every database the keys can reach |
| `researchQuestions` | Up to four questions, answered one by one in the conclusion |
| `visualSummary`, `visualSummaryNote` | The artifact (`concept-map`, `flowchart`, `table`, `bullets`, `custom` or `none`) and what it should show |
| `maxResultsPerSource` | 1 to 50, default 5 |
| `peerReviewOnly`, `dualScreening`, `citationChaining` | The form's advanced options |
| `acceptFlaggedInput` | `true` to start a run whose fields read like instructions to the model (see below) |

A start goes through the same checks as the form. `ReviewApi.Start` builds the same `ReviewRequest`, runs `ReviewInputGuard.Check` on it, and starts `RunReviewAsync`, which waits for a run slot in `RunCoordinator` like every other run. When a field holds an instruction-like phrase, the form asks once before it starts. The API answers 422 with the phrases instead, and the caller sends the request again with `acceptFlaggedInput: true`; the phrases are then written to the protocol, as with the form. The human screening check is not offered, because nobody is on the page to do it.

The status counts the seven steps the run view shows (`ReviewSteps`): `step` is 0 while the run waits for a slot, 1 to 7 while it works, and 7 when it is done, with `stepName` and `percent` beside it. It reads the live counters while the run is going here and the ledger otherwise. Once the run is complete, `verdicts` holds the tally of the citation checks and `downloads` the archive, the references as BibTeX and RIS, every screened record and the protocol. Anyone with a run's id can read its status, as anyone with its link can open its page. The edit key in the answer to a start is the only one there is, as on the form.

## The report as data

`GET /api/v1/reviews/{id}/report` returns what the Review output page shows, read from the run's files only: `prisma-report.json`, `citation-audit.json` and the ledger. Nothing is written or asked of the model for the API, and text is cleaned of its markers just as the page cleans it (`SpecMatrix.ScrubAnchors`). `ReviewApi.Report.cs` builds it.

| Part | What it holds |
|---|---|
| `title`, `abstract` | As on the page |
| `sections` | Every section in the order of `ReportOutline.Systematic`, with its id, heading, level (2 for a theme under section 4) and text; the included studies and the appendix are tables on the page, so their text is empty and their data is in `references` and `prisma` |
| `citations` | Every checked citation: reference number, section, sentence, verdict, reason, what the check could see, and the quote when it was found word for word (`quoteVerified`), with the source's link |
| `references` | The reference list, numbered as on the page, with the DOI and a web address (only `http` or `https`, `IsWebAddress`) |
| `prisma` | The counts of the PRISMA 2020 flow diagram (`PrismaFlowCounts`) |
| `appVersion`, `protocolFingerprint`, `protocolSha256` | The app version that made the run and the fingerprints of its protocol |

The OpenAPI description at `/openapi/v1.json` is made by `Microsoft.AspNetCore.OpenApi` from the routes themselves, so it cannot drift from them. It covers only the routes under `/api/v1`, and it is mapped behind the same switch.

Every error is a problem detail (RFC 9457, `application/problem+json`) with a title and a sentence in plain English. `ApiProblems.cs` holds all of them.

| Status | When |
|---|---|
| 400 | The body is missing, not JSON, larger than 64 KB, or a field is wrong; `errors` lists each one |
| 401 | There is no usable key in `X-Mistral-Key` |
| 404 | No run with this id, or a run of another kind of review |
| 409 | The report was asked for before the review was complete |
| 422 | A field reads like an instruction to the model; `flags` lists them |
| 429 | Too many calls or too many reviews started; `Retry-After` says when to try again |
| 503 | The server runs a local model, so a review could not use the caller's key |

## The MCP server

The MCP server lets another assistant run a review as a tool. It speaks the Model Context Protocol over Streamable HTTP at `/mcp`, with the official C# SDK (`ModelContextProtocol.AspNetCore`), behind the same switch as the REST API. It is stateless: every call stands on its own, and nothing from one caller is kept for the next. The code is in `AuBtechReviewAgent/Mcp/`.

| Tool | What it does |
|---|---|
| `start_literature_review` | Starts a review with the form's fields and returns the run id and edit key at once |
| `get_review_status` | The stage, the step of seven, the counts and, when done, the verdicts and the downloads |
| `get_review_report` | The title, the abstract, the verdicts, every section, and the flagged citations (partly or not supported) |
| `get_review_file` | `bibtex`, `ris` or `main.tex` as text, or the address of the `archive` |

Each tool calls `ReviewApi`, the same methods the REST API calls, so the checks, the limits and the key handling exist once. A problem comes back as a tool error with the same plain-English title and detail. A review takes minutes, so the start tool returns at once and the assistant polls the status tool. MCP answers are never compressed, because a start carries the edit key.

The caller's MCP client sends its own Mistral key in the `X-Mistral-Key` header, as for the API. A client that reads a JSON configuration, such as Claude Code (`.mcp.json`), would add the server like this, with the key taken from the caller's own environment:

```json
{
  "mcpServers": {
    "traceableai": {
      "type": "http",
      "url": "https://au-btech-literature-review-agent.dk/mcp",
      "headers": { "X-Mistral-Key": "${MISTRAL_API_KEY}" }
    }
  }
}
```

### A "/literature-review" command in another tool

Lau's other solution can offer a "/literature-review" command on top of this server. In Claude Code a command is a Markdown file, here `.claude/commands/literature-review.md`, whose text becomes the prompt and whose `$ARGUMENTS` is what the user typed after the command:

```markdown
Run a systematic literature review with the TraceableAI MCP server on: $ARGUMENTS

1. Turn the request into search words, inclusion criteria and, if given, an objective,
   years and up to four research questions. Ask me if the inclusion criteria are unclear.
2. Call start_literature_review. Keep the run id and the edit key.
3. Call get_review_status every 30 seconds until the stage is Complete, Failed or Cancelled.
   Tell me the step (of seven) when it changes.
4. When it is complete, call get_review_report. Give me the title, the abstract, the verdicts,
   and every flagged citation with its sentence, reason and quote.
5. Offer the BibTeX through get_review_file, and the link to the review's page.
```

The tools treat what the command sends as data, as the form does: the fields pass the input guard, and a field that reads like an instruction to the model is refused until the caller confirms it with `acceptFlaggedInput`.

## The caller's key

Every start brings the caller's own Mistral key. The other keys are optional headers, as in the form's own keys: `X-Elsevier-Key`, `X-Ieee-Key` and `X-Semantic-Scholar-Key`. Each key is cleaned with `SecurityUtility.SanitizeApiKey`, as the form cleans it.

There are no free reviews through the API. The daily free quota is for the web form only, and without a key nothing starts. With a key, the engine's `ResolveKeys` uses it and never falls back to the server's Mistral key. A database key the caller leaves out falls back to the server's, as on the form. When the server runs a local model (`Llm:Provider` is `OpenAICompatible`), the caller's key would not be used at all, so the API refuses to start a review.

The key lives in the run's `ReviewRequest` and in the chat service made from it, for that run only. It is never written anywhere. The protocol is written from the request without its keys, the ledger and `llm-calls.json` hold no request, and log lines carry the run id but not the request. `ApiCallerKeys.ToString` prints no key, so a log line or an exception that names the object cannot leak one. The rate limiter counts calls per key by an HMAC with a secret that exists only in memory (`ApiKeyFingerprint`), the way `RunQuotaService` counts addresses. The test `A_review_runs_to_the_end_with_the_callers_key_and_the_key_is_never_written` runs a whole review through the API with a recognisable fake key. It then searches every file in the run folder, every file in its archive and every captured log line for it, and checks that the model was only ever made with the caller's key.

## Limits

Two limits guard the API and the MCP server. Each call counts once per client address and once per key, at most 120 a minute each, on every address under `/api/v1` and `/mcp` (`ApiEndpoints.AddReviewApi`, a chained global limiter that lets every other address through). Starting a review counts again, per key and per address, at most `Quota:OwnKeyRunsPerHour` an hour, the same number the form allows with an own key. Both use ASP.NET Core's rate limiter, and neither holds a key or an address in plain text.

## Security notes

The API adds two addresses and changes none of the rules in `AGENTS.md`. What a caller sends is data: it passes `ReviewInputGuard`, it goes into prompts only through `PromptSafety`, and every model answer is still checked in code. Run ids are GUIDs in the route (`{id:guid}`), so no caller text reaches a path. The log lines the API writes hold only a run id and an exception's type. The answers are sent with `Cache-Control: no-store`, because a start carries the edit key. For the same reason the answer to a start is never compressed (`ResponseCompressionSetup.DoNotCompress`): over HTTPS, compressing a secret next to text the caller chose opens the BREACH attack.
