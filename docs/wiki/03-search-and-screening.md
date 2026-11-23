# 3. Search and screening

This page covers the first half of a run: turning a question into a list of screened records. The code is in `PrismaReviewEngine.Search.cs`, with the search strings in `PrismaReviewEngine.SearchStrings.cs` and citation chaining in `PrismaReviewEngine.Chaining.cs`, and in `PrismaReviewEngine.Screening.cs`, with the prompts in `PrismaReviewEngine.ScreeningPrompt.cs` and the human review in `PrismaReviewEngine.ScreeningReview.cs`, with one class per bibliographic source.

## From question to candidates

```mermaid
flowchart LR
    Q["Query, objective,<br/>inclusion criterion"] --> P["GenerateSearchPerspectivesAsync<br/>(model, temperature 0.2)"]
    P --> SS["1 + up to 3 search strings"]
    SS --> F1["FetchSourceAsync: arXiv"]
    SS --> F2["FetchSourceAsync: OpenAlex"]
    SS --> F3["FetchSourceAsync: ..."]
    F1 & F2 & F3 --> RAW["SourceResponses/<br/>raw responses + SHA-256"]
    F1 & F2 & F3 --> AD["Admit (in source order)"]
    AD -->|duplicate| RM["RemovedBeforeScreening<br/>with reason"]
    AD -->|outside year range| RM
    AD -->|over per-source cap| RM
    AD -->|kept| C["candidates"]
    C --> SC["ScreenCandidatesAsync"]
```

**Search strings.** `GenerateSearchPerspectivesAsync` asks the model for up to three alternative phrasings (a terminology variant, a narrower sub-topic, a methodology framing), in the spirit of STORM's multi-perspective questioning. If the call fails, the run carries on with the original query alone. The strings are appended to `protocol.md` as a dated amendment, and together with the criteria and sources they go into `ProtocolHash`.

The reviewer can also see and edit the strings before the run. "Preview search strings" on the form calls `PreviewSearchStringsAsync`, which makes the same model call with the key the run would use (a page gets five previews). The reviewer can change, remove or add strings, at most four besides the query, and they are sent as `ReviewRequest.SearchStrings`. `ReviewInputGuard` cleans them like every other field (normalised, at most 200 characters each, instruction-like phrases flagged, repeats of the query dropped). A run with `SearchStrings` set does not ask the model at all: it searches the query followed by those strings, sets `ReviewState.SearchStringsReviewed`, and the protocol amendment and the methods text (PRISMA item 7) say that the reviewer approved them. An empty list means the query alone.

**Saturation.** After the search, `SearchSaturation.Compute` goes through the strings in the order they were run and counts, for each, the records no earlier string had found (by identifier, DOI or normalised title, as in duplicate removal). The result is kept as `ReviewState.SearchStringYields`. `SearchSaturation.Hint` turns it into one sentence for the dashboard funnel: when the last string adds at most a tenth of what it retrieved, the search is called close to saturation; otherwise another phrasing or citation chaining may still find studies. A string that was never run because every source had already filled its cap is shown with a dash and named. The methods text gives the same numbers. This is a hint about the phrasings, not about databases that were not searched.

**Querying sources.** `FetchSourceAsync` runs the search strings against one source, one after another, and stops as soon as the source has returned enough distinct records to fill its cap (`MaxResultsPerSource`, split across the strings). All sources run at the same time. Every response is looked up in the `ReviewCache` first (key: source, string and cap; kept 24 hours); a response that is actually used is saved as it was received in `SourceResponses/`, and its SHA-256 goes into the `PlatformSearchLog`. A source that fails is logged as "Faulted" with a sanitised message and the run goes on without it.

**Admitting records.** The results are then processed in a fixed order, source by source and string by string. `Admit` puts every identified record into exactly one bucket: duplicate (same source id, DOI or normalised title as a record seen earlier), outside the year range (records with no year are kept), over the per-source cap, or candidate. Each removal is written to `RemovedBeforeScreening` with its reason, which is what makes the PRISMA flow diagram add up and lets the Review Output page show "Duplicates: show". Because the cap is checked last, it counts only the records that go on to screening: a source can show more records identified in the flow diagram than its cap. The methods text says so, so the two do not seem to disagree (issue #150).

## The sources

Every source implements `IAcademicSource` (`IAcademicSource.cs`): a `SourceName`, `FetchPapersAsync(query, maxResults)` returning `List<AcademicPaper>`, and `LastRawResponses`, the raw bodies of the last call.

| Class | Key needed | Notes |
|---|---|---|
| `ArxivSource` | no | Atom XML API. All requests in the app go one at a time through a static gate, 3 s apart, through `OpenSourceHttp`'s retries, because arXiv answers bursts with 503. The DOI is the published version's (`<arxiv:doi>`) when the authors gave one, and otherwise the arXiv DOI (`10.48550/arXiv.<id>`) |
| `OpenAlexSource` | expected | Also implements `ICitationGraph`, used for citation chaining; abstracts are rebuilt from OpenAlex's inverted index. Sends `OpenSources:OpenAlexApiKey` as an `Authorization` header; without a key most requests are refused once the small keyless budget is used up |
| `SemanticScholarSource` | optional | All requests in the app go one at a time through a static gate, 1 s apart with a key and 3 s without, because the API rate-limits hard |
| `CrossrefSource` | no | Abstracts arrive as JATS XML and are stripped to text |
| `ElsevierSource` | yes | Scopus / ScienceDirect |
| `IeeeXploreSource` | yes | IEEE Xplore API |

The open sources share `OpenSourceHttp` (`OpenSourceHttp.cs`): one `HttpClient` with a `User-Agent` that names the project, and up to five attempts on rate limits and server errors, waiting 3, 6, 12 and 24 seconds (or what the server's `Retry-After` asks for, up to 30 seconds). A server that asks for a longer wait, as OpenAlex does once its daily budget is used up, is not asked again: the call fails at once with the wait in its message, instead of spending two minutes on attempts that are refused again. OpenAlex and Crossref also get the contact e-mail from `OpenSources:ContactEmail`, which puts the app in their faster "polite" pool. `SourceCatalog.cs` holds the list of sources the dashboard offers and which kind of key each needs.

## Screening one record

```mermaid
flowchart TD
    A["ScreenOneAsync(paper)"] --> S["PromptSafety.Scan<br/>title, abstract, venue"]
    S --> PR{"peer-reviewed<br/>only?"}
    PR -- yes --> PF["IsPeerReviewedAsync"]
    PF -- not peer reviewed --> X1["Excluded<br/>(pre-screening)"]
    PR -- no --> F["ScreenWithCacheAsync: first prompt"]
    PF -- peer reviewed --> F
    F --> D{"DualScreening?"}
    D -- no --> AO["ApplyOutcome"]
    D -- yes --> SEC["ScreenWithCacheAsync: second prompt"]
    SEC --> AO
    AO --> AG{"passes agree?"}
    AG -- yes --> DEC["decision of the first pass"]
    AG -- no --> INC["Included + flagged:<br/>'the two screenings disagreed'"]
    DEC & INC --> U["Uncertain if: disagreement,<br/>low confidence, no abstract,<br/>or injection flags"]
    U --> LED["ScreeningLog in the ledger"]
```

`ScreenCandidatesAsync` screens several records at a time (`Llm:FastParallelism` when a fast model is set, else `Llm:QualityParallelism`), but applies the outcomes to the ledger strictly in candidate order, saving the ledger after each one. That is why the dashboard counts move one record at a time even though the calls overlap.

`ScreenPaperAsync` is the single place where the screening prompt is built; the command-line evaluation tool uses the same method, so it measures exactly what a run does. The paper's title, authors, date, venue and abstract are passed inside `PromptSafety.Wrap` markers, with an instruction that text inside them is data, never instructions. The **second** screener is a different prompt, not a repeat: it works through the exclusion criterion first and then looks for positive evidence for inclusion. Both run at temperature 0 and return JSON that `ScreeningAnswer.Validate` checks (decision exactly "Included" or "Excluded", non-empty reasoning, confidence high, medium or low, and for an exclusion an optional `exclusionReason`).

**Exclusion reasons.** When the model excludes a record it names the main reason as one of four groups in `ExclusionReasons`: off topic, inclusion criteria not met, matches an exclusion criterion, or too little information. The group is stored as `ScreeningLog.ExclusionReason`; the peer-review filter sets "not peer reviewed", and a reviewer who changes a decision to Excluded sets "excluded by the reviewer". An answer without a reason is still valid and counts as "reason not recorded", so older cached answers and runs keep working. `ExclusionReasons.Count` groups the excluded records for the dashboard funnel, the PRISMA flow diagram in `main.tex` lists the groups under "Did not meet criteria", and `ExclusionReasons.Sentence` adds them to the selection text of the report (PRISMA 2020 item 16a). The model's free-text reasoning stays in the ledger for every record. Adding the reason changed the prompt, so `ScreeningPromptVersion` is now `screening-v5`.

**Caching.** `ScreenWithCacheAsync` looks up each decision in the cache under a key built from `ScreeningPromptVersion`, which prompt (first or second), the model, both criteria and the record's metadata. A decision is only reused when all of those are identical, and it is marked `FromCache` in the ledger. Whenever you change a screening prompt, bump `ScreeningPromptVersion` so older decisions are not reused (see [page 7](07-extending-and-testing.md)).

**The multivocal review's screening** (`MultivocalScreener`) uses the same steps and answer format, and labels its two calls with the same stages, `screening` and `screening-second`, so with `Llm:FastModel` set they go to the fast model as the systematic review's do, inside the runner and outside it (see [page 8](08-setup-and-configuration.md)). Its cache key and the `Model` in `multivocal-screening.json` name the model that answers each stage, so a decision of the main model is never reused as the fast model's. A new multivocal plan looks at the top 20 hits of each search string (`MultivocalPlan.DefaultTopHits`); the paper looks at 100, and the reviewer can raise it on the form, but every hit is screened, many of them twice, so a smaller start keeps a run cheap without changing the method. The ledger records the rule as applied, with its number. The built-in example plan is kept small as well: two grey search strings, four grey searches (Stack Exchange, GitHub, OpenAlex grey literature and backlinks) and, for a multivocal plan, only the academic databases that need no key (OpenAlex, Semantic Scholar and arXiv).

**Targeted second screenings in the multivocal review** (`MultivocalSecondScreening`, approved on 23 November 2026 to cut the cost). The systematic review screens every record twice; the multivocal review screens every source once and a second time only where it matters, following the rapid-review practice of single screening with a verified sample. A source gets a second, independent screening (`screening-second`) when the first screening (a) included it, (b) was unsure (low or medium confidence, no confidence given, or no summary to judge by), or (c) `PromptSafety.Scan` found text that looks like instructions, and (d) when it falls in its pool's random audit sample. The sample is 20% of each pool, rounded up, and at least 10 sources, so a pool of ten or fewer is screened twice whole. It is drawn from every source the first screening decided, whatever it decided: a sample of exclusions alone would give the first screening one answer only, and Cohen's κ would be 0 or undefined by construction. Its extra cost is therefore the share of the first screening's other exclusions that falls in it. The draw ranks the sources of a pool by the SHA-256 of `seed|address`, lowest first, with the first eight characters of the run's id as the seed, so anyone can draw it again.

| What | Where it is on file | How it is reported |
|---|---|---|
| Why a source was screened again | `SecondReason` (`included`, `uncertain`, `flagged`) and `AuditSample` on each source in `multivocal-screening.json` | Counted by reason; the targeted agreement as "X of Y agreed", never in κ |
| The draw | `AuditSeed`, `AuditShare`, `AuditMinimum` in the same file; the rule and the seed in the protocol | The method names the seed and the rule |
| Agreement | `Kappa`, `KappaGrey`, `KappaFormal`, over the audit sample only | κ with the sample size, per pool and for both; both screenings are the same model at temperature 0, so κ measures its stability, not agreement between people |

A disagreement keeps the source and flags it, as before; a failed call, first or second, leaves the source "Error". The prompts are unchanged, but their versions were raised (`grey-screening-v2`, `formal-screening-v2`), so no answer cached under the rule of two screenings each is reused. The method, the protocol, the flow diagram ("Screened: n, of which twice: m"), the funnel on the run view, the abstract's facts, the guidelines table (G10) and the statement on AI use all take their counts from `MultivocalSecondScreening.Count` through `MultivocalScreeningText`. A run screened before the rule has no `AuditSeed` and keeps its old words: every source screened twice, κ over all of them.

**Applying the outcome.** `ApplyOutcome` turns the one or two answers into a `ScreeningLog`. When the two passes disagree, the record is included and flagged, as in human dual screening: excluding it would hide the disagreement. Confidence is the lower of the two. A record without an abstract is always "low" and its reasoning says so, because the model has only the title to go on. The prompt for such a record shows "(none: this record has no abstract)" and tells the model to judge on the title and venue only. A reason or summary that still describes an abstract ("The abstract suggests a simulation study") is sent back once by `ScreeningAnswer.ValidateWithoutAbstract`; saying that there is none passes. If the model does it again, the answer is kept and its reason starts with a warning that this part was not read from the record (issue #132). This is `screening-v6`. A decision is marked **uncertain** when the passes disagreed, confidence is low, or `PromptSafety.Scan` found instruction-like text such as "ignore previous instructions". Uncertain decisions are the ones shown first in the human review. If the model call itself fails, the record is logged as "Error: not screened" and counted, never silently dropped. When all records are screened, `FinishDualScreeningStats` computes Cohen's kappa between the two passes with `ScreeningConfusion` (`ScreeningEvaluation.cs`).

**Kappa next to high agreement.** Cohen's kappa takes away the agreement expected by chance. When nearly every record gets the same decision, almost all agreement is expected, so kappa can be 0.00 while the screenings agree on 3 of 4 records. A reader takes that for a mistake, so `MethodsSectionWriter.KappaNote` adds one sentence that says why whenever kappa is under 0.2 and at least 70% of the records agree (issue #122); the methods text and the flow figure on the report page both show it. The methods text names the model through `MethodsSectionWriter.ModelInBrackets`, which puts a name that already has brackets, such as "mistral-small-latest (Mistral API)", into one pair: "(mistral-small-latest, Mistral API, temperature 0)" (issue #120).

## Citation chaining

`ChainCitationsAsync` runs one round of backward and forward snowballing. The seeds are the records included from the database search that have a DOI. For each seed, `OpenAlexSource.GetCitationNeighboursAsync` returns up to `OpenSources:ChainingPerPaper` references and citing papers, looked up several seeds at a time (typed local function `LookUp`, throttled like screening). The results go through the same `Admit` and `ScreenCandidatesAsync` as the database search, with origin "citation chaining", so they are de-duplicated against everything seen before and screened the same way. Their raw responses are saved as `SourceResponses/chain-NN-*.json`, and the PRISMA flow reports them as "identified via other methods".

**Seed studies.** Before ordinary chaining, `ChainFromSeedsAsync` (`PrismaReviewEngine.SeedChaining.cs`) builds out further from the most-cited included studies. The rule is in `CitationSeeds` (`Search/CitationSeeds.cs`), shared by every kind of review, and no model takes part. Each source keeps its citation count on the record (`AcademicPaper.CitedByCount`, copied to `ScreeningLog.CitedByCount`): OpenAlex `cited_by_count`, Semantic Scholar `citationCount` and Crossref `is-referenced-by-count`; arXiv gives none and the Elsevier and IEEE Xplore adapters do not read one yet, so their records stay null. `CitationSeeds.Select` ranks the included database records that have a DOI, a count and a year by citations per year, the count divided by the years from publication to the year of the run with the publication year counting as one, so a study from last year can beat a classic. Ties go to more citations, then the newer study, then the record id. The top `OpenSources:SeedStudies` become seeds. Seeds come only from records already included at screening, so a highly cited paper that is off topic is never built out from. `BuildOutAsync` then asks OpenAlex, one seed at a time, for a pool of up to 100 referenced works and the 100 most cited citing works (`GetCitationPoolAsync`, one request per direction), ranks each pool the same way in code, and keeps the top `OpenSources:SeedChainingPerDirection` of each. The neighbours go through `Admit` and `ScreenCandidatesAsync` with origin "seed chaining". Ordinary chaining then runs for every other included record with a DOI, so a seed is not chained twice; a seed whose look-up failed is chained the ordinary way.

Everything is on file. `ReviewState.SeedChaining` holds the rule's numbers (the reference year, how many seeds and neighbours, how many included records had no count) and every seed with its count, citations per year, rank and what it gave; `ReviewStats.IdentifiedViaSeedChaining` counts what the seeds found, inside `IdentifiedViaCitations`. Raw answers are saved as `SourceResponses/seed-NN-*.json` with their SHA-256, under the source name "OpenAlex (seed chaining)". The flow diagram splits "other methods" into the seeds and the other included studies (`PrismaFlowDiagram.OtherMethods`), `MethodsSectionWriter.SeedChaining` writes the method sentences, the protocol names the seed rule, the AI-use statement says the seeds were chosen in code, and `SeedStudiesTable` adds a table of the seeds to the appendix of main.tex. The screening check shows each record's citation count. With citation chaining off, or `OpenSources:SeedStudies` at 0, nothing of this happens and a run is as before.

## Human review of the screening

With the "Let me check the screening decisions" option, the run stops after screening. `AwaitScreeningReviewAsync` saves the ledger with the stage `AwaitingScreeningReview` first and only then opens a gate in the `RunCoordinator`, so anyone who sees the gate open also finds the ledger saying the run is waiting. The dashboard shows `ScreeningReviewPanel.razor`, which lists uncertain decisions first. The user's choices come back through `SubmitScreeningReview` as `ScreeningOverride` records, and `ApplyScreeningReview` changes the decisions and PRISMA counters and records `HumanReviewed` and `HumanOverrides`. The run does not hold a slot while it waits, so a review left open overnight does not block other users.
