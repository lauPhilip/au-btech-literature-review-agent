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
* [ ] **Multi-Agent Screening Consensus:** Implement a dual-agent check where two different LLM prompts debate ambiguous papers to reduce selection bias.
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
