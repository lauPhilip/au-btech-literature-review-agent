# 9. The API and the MCP server

Other tools can start a systematic review and follow it without the website. This page covers the REST API under `/api/v1`, how it keeps the caller's key out of every file, and the limits that apply to it. The code is in `AuBtechReviewAgent/Api/`.

## The switch

The API is off in production until Lau turns it on. `Program.cs` reads the switch as `builder.Configuration.GetValue("Api:Enabled", builder.Environment.IsDevelopment())`, so it is on in Development and off everywhere else unless `Api:Enabled` is `true` in the server's configuration. The default lives in code, not in `appsettings.json`. With the switch off, `Program.cs` neither registers the API's services nor maps its routes, so every address on this page answers 404.

## Starting a review and following it

| Call | Answer |
|---|---|
| `POST /api/v1/reviews` with the caller's key in `X-Mistral-Key` | 202 with the run id, the edit key, the stage and the addresses to follow |
| `GET /api/v1/reviews/{id}` | 200 with the stage, the step of seven, the progress, the counts and, when done, the verdicts and the downloads |

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

Every error is a problem detail (RFC 9457, `application/problem+json`) with a title and a sentence in plain English. `ApiProblems.cs` holds all of them.

| Status | When |
|---|---|
| 400 | The body is missing, not JSON, larger than 64 KB, or a field is wrong; `errors` lists each one |
| 401 | There is no usable key in `X-Mistral-Key` |
| 404 | No run with this id, or a run of another kind of review |
| 422 | A field reads like an instruction to the model; `flags` lists them |
| 429 | Too many calls or too many reviews started; `Retry-After` says when to try again |
| 503 | The server runs a local model, so a review could not use the caller's key |

## The caller's key

Every start brings the caller's own Mistral key. The other keys are optional headers, as in the form's own keys: `X-Elsevier-Key`, `X-Ieee-Key` and `X-Semantic-Scholar-Key`. Each key is cleaned with `SecurityUtility.SanitizeApiKey`, as the form cleans it.

There are no free reviews through the API. The daily free quota is for the web form only, and without a key nothing starts. With a key, the engine's `ResolveKeys` uses it and never falls back to the server's Mistral key. A database key the caller leaves out falls back to the server's, as on the form. When the server runs a local model (`Llm:Provider` is `OpenAICompatible`), the caller's key would not be used at all, so the API refuses to start a review.

The key lives in the run's `ReviewRequest` and in the chat service made from it, for that run only. It is never written anywhere. The protocol is written from the request without its keys, the ledger and `llm-calls.json` hold no request, and log lines carry the run id but not the request. `ApiCallerKeys.ToString` prints no key, so a log line or an exception that names the object cannot leak one. The rate limiter counts calls per key by an HMAC with a secret that exists only in memory (`ApiKeyFingerprint`), the way `RunQuotaService` counts addresses. The test `A_review_runs_to_the_end_with_the_callers_key_and_the_key_is_never_written` runs a whole review through the API with a recognisable fake key. It then searches every file in the run folder, every file in its archive and every captured log line for it, and checks that the model was only ever made with the caller's key.

## Limits

Two limits guard the API. Each call counts once per client address and once per key, at most 120 a minute each, on every address under `/api/v1` and `/mcp` (`ApiEndpoints.AddReviewApi`, a chained global limiter that lets every other address through). Starting a review counts again, per key and per address, at most `Quota:OwnKeyRunsPerHour` an hour, the same number the form allows with an own key. Both use ASP.NET Core's rate limiter, and neither holds a key or an address in plain text.

## Security notes

The API adds two addresses and changes none of the rules in `AGENTS.md`. What a caller sends is data: it passes `ReviewInputGuard`, it goes into prompts only through `PromptSafety`, and every model answer is still checked in code. Run ids are GUIDs in the route (`{id:guid}`), so no caller text reaches a path. The log lines the API writes hold only a run id and an exception's type. The answers are sent with `Cache-Control: no-store`, because a start carries the edit key.
