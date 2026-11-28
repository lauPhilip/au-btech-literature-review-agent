# Development Roadmap & Objectives

## 1. "Peer Review Only" Toggle & Filter Step
*  [X] **UI Frontend:** Add a toggle switch/button for "Peer Review Only" in the application interface.
* **Post-Retrieval Filtering Pipeline:** * Create a distinct evaluation step immediately following the raw API search.
    * [X] Parse the metadata (e.g., ScienceDirect content types) or use an intermediate LLM check to verify peer-review status.
    * [X] Track and log strict validation metrics: Record exactly **X** papers passed as peer-reviewed and **Y** papers excluded for failing the threshold.
    * [X] Update the `ReviewState` and `ReviewStats` structures to display these specific metrics on the frontend progress bars.

## 2. Journal Quartile (Q1/Q2) & Conference Ranking Metrics
* [X] **Data Enrichment:** Integrate a lookup mechanism (via Scimago API, CORE rankings, or a local database mapping) into the ingestion pipeline.
* [X] **Data Model Updates:** Add ranking properties (`Quartile`, `ConferenceRating`) to `IncludedPaperMetricRow` and `ReviewState`.
* [X] **LaTeX Manuscript Integration:** * Add a new sub-section under **Section 3: Data & Collection Metrics** titled `Study Quality & Tier Distribution`.
  [X]  * Write text blocks explaining the overall quality and distribution layout of the selected material (e.g., *“Of the included data assets, 70% originate from Q1 indexed sources...”*).

## 3. Expand Academic Source Gateways
Implement dedicated API client adapters implementing the `IAcademicSource` interface for the following platforms:
* [-] **IEEE Xplore API:** For structured engineering and core framework papers.
* [X] **Google Scholar Gateway:** To catch wider indexing gaps.
* [X] **ResearchGate Scraping/API Wrapper:** To capture specialized institutional research prints.
* [ ] **Enhanced arXiv Adapter:** Deepen the current parser to handle clean category filter hooks more robustly.

## 4. Future Backlog Features
* [X] **Multi-Agent Screening Consensus:** (done as dual screening, section 9) Implement a dual-agent check where two different LLM prompts debate ambiguous papers to reduce selection bias.
* [ ] **Automated Citation Chaining:** Automatically fetch and screen the reference lists of highly relevant included papers (backward and forward citation tracking).
* [ ] **Live Document Workspace Sync:** Keep the local folder structure (`/PapersWorkspace`) automatically mirrored with a human-in-the-loop sorting dashboard.

## 5. STORM-Inspired Improvements (this pass)
* [X] **Multi-Perspective Search:** Before querying sources, the engine now asks the LLM to survey the topic from a few distinct angles (terminology, sub-topic, methodology) and issues every source query once per perspective, deduping by paper ID/title. `ReviewStats.DuplicatesRemoved` and `PlatformSearchLog.QueryUsed` make the expanded search auditable in `transparent-process.json`.
* [X] **Grounded Outline Stage:** Before writing the PRISMA narrative fields, the engine now generates a claim-to-citation outline from the retrieved source chunks (`grounded-outline.txt` in the workspace archive) and requires `synthesisResultsItem`/`discussionItem` to carry inline `[n]` citation markers tied to the run's reference list, instead of uncited prose.
* [X] **Fixed field-order bug in GoogleScholarSource/ResearchGateSource** that silently swapped `PublishedDate` and `JournalSource` for every Scholar/ResearchGate result, corrupting citations and peer-review checks for those two sources.
* [X] **Reliable workspace download:** the "Download Review Footprint (.ZIP)" button now hits a real HTTP endpoint (`/api/workspace/{sessionId}/archive`) instead of pushing the zip through Blazor Server's SignalR connection as base64, which silently failed once a run had real downloaded source PDFs in it.

## 6. STORM-Inspired Improvements (round 2 - live-test feedback)
* [X] **Hard per-source `maxResults` cap:** `maxResults` (e.g. "3 results per source") is now a true ceiling per source, not per search-perspective query. The budget is split across the generated perspectives (`perPerspectiveCap`), the perspectives loop breaks early once a source hits its cap, and a final safety-net truncation (`ReviewStats.CappedBeyondMaxResults`) guarantees no source can ever contribute more than `maxResults` papers even if an adapter over-returns. Makes runs cheap and predictable to test with small values like 3.
* [X] **Citation traceability guardrail:** every inline `[n]` citation marker the model writes into `synthesisResultsItem`/`discussionItem` is now validated against the run's actual reference list size and stripped if out of range (fixing hallucinated citations like `[56]` against a 53-entry list). Every stripped marker is logged to a new `citation-audit.json` artifact (`ReviewStats.InvalidCitationsStripped`), shipped in the workspace archive alongside `grounded-outline.txt`, so citation traceability failures are visible rather than silently wrong.
* [X] **Broadened venue-extraction regex:** the italicized-venue matcher now also recognizes "In *Conference Name*" proceedings-style citations (previously only ". *Journal Name*" journal-style citations matched), fixing spurious "Indexed Source Venue" placeholders for conference papers with a valid parseable venue.
* [X] **Removed repetitive per-paper narrative:** the Data & Collection Metrics section no longer emits one near-identical sentence per included paper (a wall of ~40 "was pulled from the un-reviewed preprint repository archive..." sentences on a full run). Replaced with a single aggregate breakdown sentence (Q1 / other-quartile / conference / preprint counts), pointing to the existing Figure 3.4 and Table 3.1 for the full per-paper detail.
## 7. Traceability fixes, run quota and source selection
* [X] **One reference numbering:** the reference list shown to the model, the `[n]` markers, Table 3.1 and the LaTeX bibliography are all numbered from the same ordering (`IncludedPaperMetricRow.ReferenceNumber`). Previously the bibliography followed screening order, so correctly cited numbers pointed at the wrong papers in `main.tex`.
* [X] **References from source metadata:** `ApaCitationBuilder` builds APA references from the API record instead of asking the model, so no invented authors, years, page ranges or `doi.org/XXX` placeholders. arXiv papers get their real DataCite DOI (`10.48550/arXiv.<id>`).
* [X] **Adapter fixes:** IEEE Xplore had the date and venue swapped and an author check that could never succeed (every IEEE paper was credited to "IEEE Research Consortium"); Scholar/ResearchGate defaulted unknown years to the current year and added placeholder authors.
* [X] **Quartiles:** the Scimago lookup asked for `scimago_YYYY.csv` while the data is `scimagojr YYYY.csv`, so it never matched; IEEE/Scholar papers were then given "Q1" by default. Now exact normalised title match only, no default, and the data ships with the build.
* [X] **Methods from run data:** PRISMA Items 5-8 are generated by `MethodsSectionWriter` from the run's settings and ledger (no more "a researcher manually reviewed borderline cases").
* [X] **Stylistic pass guard:** the title is no longer rewritten, and rewrites that change numbers, drop citations or change length drastically are rejected and logged.
* [X] **Real protocol hash** instead of the hard-coded `cc584090`; configurable funding statement instead of a hard-coded one.
* [X] **LaTeX compiles:** fixed the invalid `-->` arrow tip, the unknown `text align` key, comma decimals on Danish-locale machines, and the fixed 2023-2026 year axis.
* [X] **Run quota:** `RunQuotaService` (3 free runs per address per day, global daily ceiling, own-key and developer tiers, persisted and hashed). Replaces the rate-limiter policy that was never attached. Forwarded headers are no longer trusted from anyone.
* [X] **Source selection:** checkboxes per source in the dashboard; unavailable sources are greyed out; placeholder API keys no longer count as keys; the selection is stored in the ledger.
* [X] **Date range applied:** the Date From / Date To fields now filter by publication year (they were previously ignored).
* [X] **Per-session progress:** progress events carry the session id, so one user's dashboard no longer shows another user's counts.
* [ ] **Deploy to Simply:** set `Quota:AdminToken`, `MISTRAL_API_KEY` and (optionally) `Report:SupportStatement` as server secrets; make sure `App_Data/` is writable.

## 8. Multi-user hardening and research features
* [X] **Concurrent downloads:** `main.tex` is built in memory; no more shared `manuscript_*.tex` files that one download could delete under another.
* [X] **Run links:** `/review/{id}` and `/spec-matrix/{id}` reopen a run after a reload, from a bookmark or on another device.
* [X] **Retention:** runs are kept 7 days (`Runs:RetentionDays`), judged by the newest file in the folder; active runs are never deleted.
* [X] **Run slots:** at most `Runs:MaxConcurrentRuns` runs call the model at once, first come first served, with the place in line shown; 429/5xx responses are retried with backoff.
* [X] **Restarts:** runs cut off by an app restart are marked "Interrupted" at startup.
* [X] **Efficiency:** Scimago data is loaded once per year into memory; arXiv and Scopus adapters share one HttpClient.
* [X] **Balanced grounding:** every included paper contributes its abstract and an equal share of relevant passages (the old first-40-chunks rule let one long PDF crowd out the rest).
* [X] **Citation support check:** per cited sentence, a verdict with a verified verbatim quote in `citation-audit.json`, summarised in the report.
* [X] **Human screening review:** optional pause after screening; overrides recorded with the model's original decision.
* [X] **PRISMA flow diagram** in `main.tex`; every identified record is now counted in exactly one box (records dropped at the per-source cap and failed screening calls used to vanish).
* [X] **BibTeX / RIS export** in the archive and as downloads.
* [X] **Run settings:** `llm-calls.json` with model, app version, temperature per stage, retries, token use and prompt hashes; screening runs at temperature 0.
* [X] **Screening evaluation tool** (`tools/ScreeningEval`) for SYNERGY/ASReview datasets.
* [X] **Open-access full text:** arXiv, the source's open-access link, then Unpaywall by DOI (see section 9).
* [ ] **Run history in a database** (see notes on PostgreSQL).

## 9. Open sources, security, run time, traceability and appraisal
* [X] **Open sources:** Google Scholar and ResearchGate via SerpApi (paid scraping) replaced by OpenAlex, Semantic Scholar and Crossref; all sources are queried in parallel.
* [X] **Citation chaining:** one round of backward and forward snowballing through OpenAlex, reported as "other methods" in the PRISMA flow.
* [X] **Local models:** `Llm:Provider = OpenAICompatible` runs the review on Ollama or any OpenAI-compatible server.
* [X] **Prompt injection:** third-party text wrapped in data markers in every prompt; instruction-like phrases flagged per record.
* [X] **Structured output:** every model answer is parsed into a typed record and validated, with one repair attempt.
* [X] **Dual screening:** two independent prompts, disagreements included and flagged, Cohen's kappa in the methods; confidence per decision; uncertain decisions shown first in the human review.
* [X] **Parallel screening and extraction** (`Llm:ScreeningParallelism`), applied to the ledger in a fixed order.
* [X] **Cache** for search responses (24 h) and screening decisions (90 days), keyed on paper, criteria, model and prompt version.
* [X] **Traceability:** protocol.md before the search, raw source responses, manifest.json with SHA-256 of every file, clickable citations on the Review Output page.
* [X] **Extraction and appraisal:** study type, method, sample, findings, limitations and MMAT 2018 with verified quotes (Tables 3.2 and 3.3).
* [X] **Web security:** Tailwind compiled at build time, Mermaid served locally, Content-Security-Policy and security headers, 40 MB PDF limit, delete-run button.
* [X] **CI:** actions pinned to commit SHAs, Dependabot, CodeQL, CSS freshness check, release workflow creating a GitHub release (Zenodo DOI once linked).
* [X] **Open source packaging:** CITATION.cff, .zenodo.json, Dockerfile, SCImago licence note; unused QuestPDF dependency removed.
* [X] **Code quality:** review engine split into search, screening, report and archive parts; ILogger instead of Console output.
* [X] **Live check of the open-source adapters** (2026-09-28): OpenAlex search and citation lookups, Semantic Scholar, Crossref and Unpaywall answered in the formats the parsers expect. Fixes from it: longer back-off for Semantic Scholar's 429s, escaped HTML in Crossref abstracts, family-first author names from OpenAlex.
* [ ] **Docker image in releases** (Dockerfile exists; not needed for the Simply deployment).
* [ ] **Second model for dual screening** (currently two prompts to the same model).

## 9. Multivocal Literature Review
* [ ] Plan and step-by-step to-do, mapped to the Garousi et al. (2019) guidelines: [docs/roadmap/mlr-todo.md](docs/roadmap/mlr-todo.md)
* [ ] First, one module per kind of review (steps M1 to M6): [docs/roadmap/review-modules.md](docs/roadmap/review-modules.md)
