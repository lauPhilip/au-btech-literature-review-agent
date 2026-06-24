# Development Roadmap & Objectives

## 1. "Peer Review Only" Toggle & Filter Step
*  [X] **UI Frontend:** Add a toggle switch/button for "Peer Review Only" in the application interface.
* **Post-Retrieval Filtering Pipeline:** * Create a distinct evaluation step immediately following the raw API search.
    * [X] Parse the metadata (e.g., ScienceDirect content types) or use an intermediate LLM check to verify peer-review status.
    * [X] Track and log strict validation metrics: Record exactly **X** papers passed as peer-reviewed and **Y** papers excluded for failing the threshold.
    * [X] Update the `ReviewState` and `ReviewStats` structures to display these specific metrics on the frontend progress bars.

## 2. Journal Quartile (Q1/Q2) & Conference Ranking Metrics
* **Data Enrichment:** Integrate a lookup mechanism (via Scimago API, CORE rankings, or a local database mapping) into the ingestion pipeline.
* **Data Model Updates:** Add ranking properties (`Quartile`, `ConferenceRating`) to `IncludedPaperMetricRow` and `ReviewState`.
* **LaTeX Manuscript Integration:** * Add a new sub-section under **Section 3: Data & Collection Metrics** titled `Study Quality & Tier Distribution`.
    * Write text blocks explaining the overall quality and distribution layout of the selected material (e.g., *“Of the included data assets, 70% originate from Q1 indexed sources...”*).

## 3. Expand Academic Source Gateways
Implement dedicated API client adapters implementing the `IAcademicSource` interface for the following platforms:
* [ ] **IEEE Xplore API:** For structured engineering and core framework papers.
* [ ] **Google Scholar Gateway:** To catch wider indexing gaps.
* [ ] **ResearchGate Scraping/API Wrapper:** To capture specialized institutional research prints.
* [ ] **Enhanced arXiv Adapter:** Deepen the current parser to handle clean category filter hooks more robustly.

## 4. Future Backlog Features
* [ ] **Multi-Agent Screening Consensus:** Implement a dual-agent check where two different LLM prompts debate ambiguous papers to reduce selection bias.
* [ ] **Automated Citation Chaining:** Automatically fetch and screen the reference lists of highly relevant included papers (backward and forward citation tracking).
* [ ] **Live Document Workspace Sync:** Keep the local folder structure (`/PapersWorkspace`) automatically mirrored with a human-in-the-loop sorting dashboard.