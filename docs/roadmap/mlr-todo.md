# Multivocal Literature Review (MLR): plan and to-do

This file plans the second review type on the start page, the Multivocal Literature Review, following the guidelines of Garousi, Felderer and Mäntylä (2019), *Guidelines for including grey literature and conducting multivocal literature reviews in software engineering*, Information and Software Technology 106, 101–121, https://doi.org/10.1016/j.infsof.2018.09.006. Page numbers below refer to that paper.

An MLR is not a systematic review with a few extra sources. It asks different questions (state of the art *and* state of the practice), searches differently, judges its sources by a different checklist, builds a systematic map before it synthesises, and reports to a different audience. So in TraceableAI it is its own path: choosing it on the start page opens its own planning form, its own run view and its own report, laid out the way Garousi et al. describe the method, not the way PRISMA describes a systematic review.

What the two paths share is the core that makes TraceableAI what it is: every statement in the report is traced to a quote from the source it cites, every model answer is checked in code, and every decision is on file. That core is reused; everything else is built for the MLR.

This file first explains what an MLR is and how its path through the app differs, then maps every step of the guidelines to what has to be built, and ends with the to-do list and the decisions to make before starting.

## 1. What an MLR is

An MLR is a systematic review that includes grey literature (GL) as well as the formal, peer-reviewed literature (p. 101–102). Garousi et al. use the Luxembourg definition of grey literature: material "produced on all levels of government, academics, business and industry in print and electronic formats, but which is not controlled by commercial publishers" (p. 102). An MLR therefore covers both the state of the art and the state of the practice (p. 104).

Grey literature is graded in three tiers by how well its expertise and its outlet control are known (Fig. 1, p. 103). The tiers matter because the quality checklist later scores them.

| Tier | Outlet control and credibility | Examples | Score in the checklist (Table 7) |
|---|---|---|---|
| White literature | Known expertise, full outlet control | Journal papers, conference papers, books | Not scored here; handled as in an SLR |
| 1st tier GL | High | Books, magazines, theses, government reports, white papers | 1 |
| 2nd tier GL | Moderate | Annual reports, news articles, presentations, videos, Q&A sites such as Stack Overflow, wiki articles | 0.5 |
| 3rd tier GL | Low | Blogs, e-mails, tweets | 0 |

The guidelines take the three phases of Kitchenham and Charters' SLR guidelines (planning, conducting, reporting; Table 3, p. 107) and only add guidance where grey literature makes a step different (p. 107). Fig. 7 (p. 108) shows the whole process: establish the need and the goal, raise the research questions, search formal and grey sources, snowball, select by voting, build a classification map, extract data, synthesise, and report to the target audience. One step in that figure has no counterpart in TraceableAI today: the systematic map (an attribute scheme per research question, refined iteratively; Table 10, p. 116).

## 2. Its own path through the app

| | Systematic review (today) | Multivocal review (to build) |
|---|---|---|
| Start | Card "Systematic Literature Review" opens `/review` | Card "Multivocal Literature Review" opens `/mlr` |
| Planning | One form: question, objective, criteria, sources, options | A planning form in the order of the guidelines: need and audience (G2), whether to include grey literature with the seven questions of Table 4 (G3), research questions with their types (G4, G5), grey-literature types and producers (G6), search engines and sites (G7), stopping rule (G8), quality threshold (G11) |
| Run view | One pipeline: search, screening, full text, extraction, synthesis, check | Two pools side by side, formal and grey literature, each with its own counts; a step for the quality checklist; a step for the systematic map, which the reviewer can edit before extraction |
| Report | PRISMA 2020 items, numbered as in the checklist | Structured as the guidelines are: planning, conducting (search, selection, quality, map, extraction, synthesis), reporting; a table showing how each guideline G1–G14 was followed; a practitioner summary next to the full research report |
| Sources in the report | Studies with MMAT appraisal | Every source with its type, grey-literature tier and quality score; findings labelled by the kind of source they rest on |
| Flow diagram | PRISMA 2020 | Two arms, formal and grey, with quality as an exclusion reason |
| Run ledger | `transparent-process.json` (`ReviewState`) | Its own ledger type, with the method recorded, so the two kinds of run cannot be confused |

The look stays the same (the shared design, header, verdict colours and citation popup), so the site still feels like one tool. The structure of each page follows the method.

### Shared core and MLR-only parts

```
Shared core (used by both paths)               MLR only (new)
  Sources/       databases and web sources       Modules/Multivocal/         the MLR module, its ledger and stages
  Llm/           model access, JSON, safety        MultivocalModule            planning → search → selection → quality
  Verification/  citation range and support         MultivocalState             → map → extraction → synthesis → report
  Runs/          edit keys, notes, metrics,          GreyQualityChecklist        Table 7, scored with quotes
                 clean-up                            SystematicMap               attributes, refinement, classification
  Report/        manifest, archive, references       MultivocalReportWriter     the report sections in code
  Pipeline/      run coordinator, progress, cache    Pages/                    planning form, run view, report page
```

Today most of the pipeline is written as parts of `PrismaReviewEngine`. Before the MLR engine can reuse the core, the parts it needs (screening one record, the citation check of a text, the archive and manifest, the run folder) have to become services that do not depend on the systematic review. That is block A below (the module design in [review-modules.md](review-modules.md)), and it changes nothing a user can see.

## 3. Every step, and what it needs

The fourteen guidelines (G1–G14) are the concrete rules. For each step the table says what the MLR can reuse from the shared core and what has to be built for it.

| Phase and step | Guideline (page) | What it asks | Reused from the shared core | Built for the MLR |
|---|---|---|---|---|
| **Planning** · process | G1 (p. 108) | Use the typical MLR process (Fig. 7) or the SLR protocol structure as a template for the protocol | Writing a protocol before any search, and its fingerprint | An MLR protocol: the planning form's answers in Fig. 7's order, with GL types, stopping rule and quality threshold |
| Planning · need | G2 (p. 108) | Find existing reviews first; plan the MLR to be useful to its audience (researchers and/or practitioners) | – | Audience and existing-reviews fields in the planning form; the audience decides the report's practitioner summary |
| Planning · include GL? | G3 (p. 109, Table 4) | Decide systematically whether to include GL, with seven yes/no questions; one or more "yes" suggests an MLR | – | The seven questions as the first step of the planning form; the answers in the protocol and the report |
| Planning · questions | G4 (p. 109) | Research questions tied to the goal and audience, objective and measurable | – | Several RQs with sub-RQs (as in Table 5), each driving search, extraction and synthesis |
| Planning · question types | G5 (p. 110, Table 6) | Consider all RQ types (existence, description, comparison, frequency, process, relationship, causality, design), knowing the sources may not answer all | – | Each RQ classified by type; a warning for types the sources are unlikely to answer |
| **Conducting** · search: what | G6 (p. 111) | Decide early which GL types and producers to cover (white papers, blogs, videos, Q&A sites, company reports, government) | – | GL types and producers chosen in the planning form and listed in the protocol |
| Conducting · search: where | G7 (p. 111) | Use general web search engines, specialised databases and websites, backlinks (snowballing for GL) and contacting people | Academic database sources, OpenAlex citation chaining, `SourceStatus` | The free site APIs (Stack Exchange, GitHub, Hacker News), grey literature in OpenAlex and Zenodo, backlink snowballing from included sources; no general web search engine (decision 1); no contacting people (decision 5) |
| Conducting · search: terms | p. 111 | Run an informal pre-search for synonyms, since GL terminology is unstandardised; consult glossaries (SWEBOK, ISTQB) | Search-string preview and approval | Separate search strings for formal and grey sources, both previewed and approved |
| Conducting · search: when to stop | G8 (p. 112) | Choose one of three stopping rules: theoretical saturation, effort bounded (top N hits), or evidence exhaustion | `SearchSaturation` | The rule chosen in planning; effort bounded by default (top 100, continuing while the last page still adds relevant hits, as in MLR-AutoTest); saturation shown per grey string |
| Conducting · selection criteria | G9 (p. 112) | Combine inclusion and exclusion criteria for GL with the quality criteria of Table 7 | Screening one record with two independent prompts, exclusion reasons | MLR screening prompts for both pools; no peer-review filter; selection may use quality items such as date and outlet |
| Conducting · selection process | G10 (p. 112) | Integrate the GL and formal selection; same effort and criteria for both; settle disagreements (voting) | Dual screening, flagged disagreements, the human review step | Both pools screened by the same criteria and shown side by side in the run view |
| Conducting · quality assessment | G11 (p. 112–114, Tables 7–9) | Score each GL source on authority, methodology, objectivity, date, position w.r.t. related sources, novelty, impact and outlet type; 20 items, each 1, 0.5 or 0; normalise to 0–1; a threshold decides inclusion (the example uses 10 of 20) | Quote-verified answers (as for MMAT) | The GL checklist with every model answer backed by a quote; computable items decided in code; the score table in the report |
| Conducting · extraction | G12 (p. 115, Table 10, Figs. 8–9) | Traceability links from every extracted item to the place in the source; enough data for each RQ; each GL document's purpose and coverage | Quote-verified extraction | An extraction form per RQ (attributes with single or multiple values), plus purpose and coverage for each GL source |
| Conducting · systematic map | Fig. 7, Table 10 (p. 108, 116) | Identify attributes, generalise and refine them iteratively into a classification map | – | A map stage the reviewer can edit before extraction; later reused by the grey literature review |
| Conducting · synthesis | G13 (p. 118) | Fit the synthesis to the data: qualitative coding (open and axial) for most GL, limited quantitative synthesis for surveys, argumentation theory (expertise, field, opinion, trustworthiness, consistency, backup evidence) to weigh opinions; balance evidence of different rigour | Coding of quote-verified findings into themes | MLR synthesis: each finding labelled with its source type and quality score; state of the art and state of the practice side by side; argumentation questions in the quality record |
| **Reporting** · style and audience | G14 (p. 118) | Match the writing to the audience; a short plain version for practitioners and a transparent version for researchers; implications; an online repository of sources | Citation check and repair, archive and manifest, reviewer notes, RIS export | The MLR report page and report writer: guideline table G1–G14, practitioner summary with a checklist like Table 12, the source repository with tiers and scores |
| Reporting · flow diagram | (not in the guidelines) | Show how many sources were found, screened and included | The flow-diagram drawing code | Two arms, formal and grey, with quality as an exclusion reason |

## 4. To-do

Each block is meant to be one pull request. The MLR card stays "coming soon" until block G is done; until then `/mlr` is only reachable in Development, so nothing half-built is shown to users.

### Block A: Free the shared core from the systematic review (no visible change)

Block A is now the module design in [review-modules.md](review-modules.md), steps M1 to M5: a module contract and registry, the run services and the grounding check as shared services, the archive split, and a test kit every module must pass. A common source record that can describe a paper or a web page (title, authors or producer, date, venue or site, URL, DOI, source kind, text) is part of M3, so screening and the citation check do not need to know which it is. The systematic review keeps passing all its tests unchanged throughout.

### Block B: The MLR skeleton

- [ ] `Modules/Multivocal/MultivocalModule.cs` (step M6 of the module design) with its stages as empty steps, and `MultivocalState` as its own ledger type (method recorded, formal and grey pools kept apart).
- [x] The preview: `/mlr` and `/mlr/{runId}` in `Modules/Multivocal/Pages/`, visible only where the `ReviewModules:Preview` setting lists the module (Development), "not found" elsewhere. Today `/mlr` shows the steps to come.
- [ ] `/mlr` becomes the planning form (block C), `/mlr/{runId}` the run view and `/mlr-report/{runId}` the report.
- [ ] The MLR card on the start page links to `/mlr` once block G is done (`Available: true`); until then it stays "coming soon".
- [x] Tests: an MLR run and a systematic run open on their own module's pages (`ReviewModules.ElsewhereFor`); `run.json` records the module and card.

### Block C: Planning (G1–G5)

- [x] Planning form in the order of the guidelines, one step per screen (`Modules/Multivocal/Pages/MultivocalStart.razor`): need and existing reviews, audience (G2); the seven questions of Table 4 (G3), with a note when every answer is "no" that a systematic review may be enough; research questions with sub-RQs (G4), each with a type from Table 6 (G5) and a warning for types the sources are unlikely to answer.
- [x] The MLR protocol (G1): the form's answers in the order of Fig. 7, written and fingerprinted before any search, with GL types, search engines and sites, stopping rule and quality threshold in points (`MultivocalProtocol.cs`). A planned run is written by `MultivocalPlanner` (run.json at stage "Planned", protocol.md, multivocal-plan.json) and shown at `/mlr/{runId}`.
- [ ] The run contract (`RunAsync`, `StagesFor`) and the multivocal entry in the test kit, once the first search runs (block D).
- [x] Tests: the protocol contains every planning answer; an empty Table 4 is refused (`MultivocalPlanTests.cs`).

### Block D: Searching formal and grey literature (G6–G8)

- [x] GL types and producers chosen in planning (G6) and recorded in the protocol (block C).
- [x] The free site sources (G7) behind one interface, `IGreySource` in `Modules/Multivocal/GreySources/`: Stack Exchange (keeping score, views and answers for the impact item of the quality checklist), GitHub (stars, forks) and Hacker News (points, comments), plus Zenodo reports, theses and white papers. Each keeps its raw answers and is timed in `SourceStatus`; only http(s) addresses are kept. dev.to is dropped: its public API has no search. Tested offline against the answer shapes the APIs document; the first live run must confirm them.
- [x] Running the grey searches of a planned run (`Multivocal/MultivocalSearch.cs`): every chosen search that is built, with every grey search string, from the run page's "Search now" button (only the browser that planned the run). Each raw answer is saved as received in `SourceResponses/` with its SHA-256; the sources found are de-duplicated by address and each keeps the searches that found it; a failing search is recorded and the others still run; searches not built yet are listed as skipped. All of it goes into `multivocal-ledger.json`, which is in the run archive. The run moves from "Planned" to "Searched".
- [ ] Caching the raw answers between runs, and the run contract (`RunAsync`, `StagesFor`) with the multivocal entry in the module test kit.
- [x] Grey literature in OpenAlex (`OpenAlexGreySource`): reports, theses and standards, with a type filter on the same free API the systematic review uses and the OpenSources contact e-mail. The citation count is kept for the impact item. "Other" is left out, since in OpenAlex it is mostly front matter and index pages.
- [x] Brave web search dropped (decision 1, revised): its API terms forbid storing or caching search results beyond transient use, and a traceable review keeps every raw answer and every source found. A plan made before it was dropped still opens; the search is listed as no longer offered.
- [ ] Videos only with a published transcript (decision 3).
- [x] Grey search strings (up to five) planned in step 4 and written into the protocol; without any, the topic is the search string.
- [ ] Formal search strings for the formal pool, previewed and approved in planning.
- [x] Stopping rule (G8), first version: effort bounded asks for the top N hits of each string (at most one page of 100), exhaustion takes the whole first page, and saturation stops a search once a string adds no new source. The rule as applied is written in words into the ledger for the methods text.
- [ ] Stopping rule, next: one more page while the last page still adds relevant sources, which needs screening (block E).
- [ ] Link snowballing from the included grey sources (backlinks, G7), through the same duplicate check as citation chaining.
- [x] A snapshot of a found source's page (`MultivocalPages.cs`, `WebPages/`): the page's main text (HTML, plain text or PDF), its address after redirects, the access date, the SHA-256 of the text and a Wayback Machine link. The text stays in the run folder (`PageText/`) and is not in the archive; `multivocal-pages.json` is (decision 2). Only pages the searches found can be fetched. For now the reviewer keeps a page from the Sources found tab; block E will keep the pages of the included sources.
- [x] `robots.txt` read as RFC 9309 says for our product token "TraceableAI" (an unreadable one means nothing is fetched), one request a second per site or its Crawl-delay (above 30 seconds the site is skipped), nothing behind a sign-in (401, 403, sign-in addresses), at most 10 MB, and only public addresses: an address that resolves to the server or its private network is refused when connecting.
- [ ] Site terms beyond robots.txt: not readable by code, so the methods text will say that only robots.txt was checked.
- [x] Tests: offline fakes for the web and Stack Exchange sources; the same page fetched twice gives the same snapshot; the stopping rule stops where it says.

### Block E: Selection and quality (G9–G11)

- [x] Screening of the grey pool (`MultivocalScreening.cs`): inclusion and exclusion criteria in the plan (G9, step 4 of the form, in the protocol); every source screened twice with the systematic review's steps and answer format (`PrismaReviewEngine.ScreeningProcedure`, `ScreeningAnswerFormat`), so both pools get the same care (G10); a disagreement keeps the source and flags it, as today; Cohen's kappa; the same title by the same producer under another address (an OpenAlex work with several DOIs) screened once. Decisions are cached and written to `multivocal-screening.json`, which is in the archive. The run goes from "Searched" to "Screened".
- [x] The reviewer's look at the flagged decisions (disagreements, low confidence), as in the systematic review's human review step: on the Sources found tab, after screening and before the quality is scored, the reviewer keeps or changes any decision with an optional note; the model's decision stays on file (`MultivocalScreener.ReviewAsync`).
- [ ] The formal pool, screened with the same criteria once its search is built.
- [x] `Multivocal/GreyQualityChecklist.cs`: the 20 items of Table 7 (authority 4, methodology 6, objectivity 4, date 1, position 1, novelty 2, impact 1, outlet type 1), scored 1, 0.5 or 0, out of 20 points (decision 4), checked against the paper on 7 October 2026.
- [x] Every point the model gives backed by a quote found in the kept page text (`MultivocalQuality.cs`), checked with the same quote check as citations; a quote not found gives no points, and for 3.3 (vested interest, asked the other way round) a lower score needs the quote. The date (from the search's record) and the outlet tier are decided in code. Links to related sources (5.1) are answered by the model with a quote, since the snapshot keeps the text and not the links.
- [x] Impact from the counts the search returned (citations, score, views, answers, stars, forks, points, comments), each with two thresholds written into the protocol; with none it scores 0 and says "not available". Saying so in the report follows with the report (block G).
- [x] The threshold from the protocol (default 10 of 20 points); a source at or above it passes, one below is marked "Below threshold" with its score, and one whose page cannot be read is "Not assessed" with the reason. Everything goes to `multivocal-quality.json`, which is in the archive.
- [x] Argumentation questions for opinion pieces (G13) in the quality record (`GreyArgument`): the model marks a source as an opinion piece, copies its main claim and answers four of the six critical questions for an expert opinion (expertise, field, assertion, evidence) with quotes checked against the page; trustworthiness is left to the reviewer, as the paper notes it cannot be judged reliably, and consistency with other experts goes to the synthesis (block F). A source whose quality could not be assessed can be tried again (`MultivocalQualityAssessor.RetryAsync`).
- [x] Tests: the checklist has Table 7's 20 items; the totals of Table 9 rank GL2, GL3, GL4, GL1, GL5 and all pass at 10 (the five pages themselves are not kept, so they are not re-scored); a point without a quote in the page is not given; a whole assessment with a fake web and model passes one source, puts one below the threshold and leaves one not assessed because robots.txt forbids it.

### Block F: Map, extraction and synthesis (G12, G13)

- [x] The systematic map (`Multivocal/MultivocalMap.cs`): the model proposes attributes from the RQs, each tied to one RQ, with values generalised over the sources that passed the quality check (single or multiple choice, or open text); the reviewer edits it on the Map tab, each saved edit kept as a new version in `multivocal-map.json` (in the archive), and fixes it before extraction. The run goes from "Assessed" through "Mapping" to "Mapped". After the first live run (8 October 2026: 14 attributes, 8 of them on RQ1, seven "Not specified" values, a "source type" attribute, overlapping attributes), the model is held to at most three attributes per question, each value in one attribute only, plain names, and nothing the run already knows; "Not specified" and similar values are removed in code, since "not stated" is recorded for every attribute; and the known facts (kind, grey or formal, site, date, quality points) are recorded by code for every source. An attribute the model still proposes for a known fact (a "Source type" for an RQ about grey and academic sources, seen in a second live run, twice in a row) is dropped in code with a note on the Map tab, instead of failing the whole proposal.
- [x] Extraction against the fixed map (`Multivocal/MultivocalExtraction.cs`): every source that passed the quality check, each attribute's values with a quote found in the kept page (a value without one is kept on file as unsupported, never used), listed values only for a closed attribute and one value for a single-choice one, "not stated" when the page says nothing; the source's purpose (G12) with a quote; the known facts and the coverage (the RQs with a stated value) in code. Written to `multivocal-extraction.json`, which is in the archive; the Map tab counts how many sources have each value. The run goes from "Mapped" through "Extracting" to "Extracted".
- [x] MLR synthesis (G13, `Multivocal/MultivocalSynthesis.cs`): per research question, the quote-verified findings grouped in code by attribute and value (the map's values are the first codes), then three small model steps: the model names one to six themes from the value groups, places the groups in them in batches of 30 (every group in a theme, or none with a reason), and names the tensions in each theme with groups from two sources or more; each finding labelled in code with its kind, outlet tier and quality points, and each theme with what it rests on (sources, tiers, mean quality, "grey literature only" or "3rd-tier grey literature only"). Written to `multivocal-synthesis.json`, which is in the archive. What the formal literature says joins once the formal search is built. The placement is checked strictly (every group placed, theme names as given, two retries); a batch that still fails is listed as not placed with a note, a theme that gets no values is left out with a note, and a tension outside its theme or within one source is removed in code with a note on the Themes tab. The first live runs showed why: one bad tension, and later a large question whose hundreds of findings the model could not all place in one answer, each failed the whole synthesis. The first complete run (770 findings, 19 themes) showed four more things, now decided in code (prompt `grey-synthesis-v3`). Values of an open attribute, such as a tool's name, are names rather than claims, so they are never asked about tensions, and the model is told that values which are merely different are not tensions. The model's text never shows the value ids it was given; code puts the values' names in their place. Findings with the map's "Other" value are not themed by the model but listed per question as not coded by the map, the sign from G12 that the map may need a new value, and a value that only says the source does not address the question ("Not addressed") is not a finding. A theme's name and description may not say how many sources support it ("the majority of sources"), since the sources are counted in code. Notes on left-out tensions are one line per theme. Writing the themes up as cited prose is the report (block G).
- [x] The citation check against the kept page text (`MultivocalPages.CheckQuote`): "found", "not found" or "no kept page", never the live page; the evidence basis is "page text". The synthesis checks every finding with it again and leaves out any whose quote is no longer in the kept text.
- [x] Tests: a theme resting on 3rd-tier grey literature only is labelled as such; a quote that is on the live page but not in the kept text fails.
- [ ] From the first complete run, two gaps in earlier steps. The same white paper was found twice under one title ("The 2026 AI Inflection Series, Chapter 18", F41 and F53), so the duplicate check of the grey sources should also compare titles. And a quote can be on the page without supporting its value ("All agents in this tool are designed to run 100% locally" was coded as prompt engineering): the extraction checks that the quote is there, but not that it supports the value, as the systematic review's support check does.

### Block G: The MLR report and run view (G14), then switch it on

- [ ] Run view: formal and grey pools side by side with their counts, the quality step with scores, the map step the reviewer can edit.
- [ ] `Multivocal/MultivocalReportWriter.cs`: report sections written in code from the ledger, following the guidelines' phases; a table showing how each guideline G1–G14 was followed; the methods text with the Table 4 answers, GL types, engines and sites, stopping rule and where it stopped, checklist and threshold, sources per tier.
- [ ] Flow diagram with two arms, formal and grey, quality as an exclusion reason.
- [ ] Practitioner summary for the audience chosen in planning (G14): implications in plain language and, where the RQs allow, a checklist like Table 12.
- [ ] The source repository: every source with type, tier, quality score, link and access date, in the report, the archive and the RIS export.
- [ ] Statement on AI use, model card, privacy page and wiki: web pages are fetched and stored as snapshots.
- [ ] Switch the card on (`Available: true`) after one real MLR has been checked by hand.

## 5. Decisions (made on 6 October 2026)

1. **Searching the web: a free mix that lasts, no general web search engine.** No general web search API is both free and stable: Brave replaced its free plan with a monthly credit in February 2026 and its API terms forbid storing or caching search results beyond transient use, Google's Custom Search JSON API closes on 1 January 2027, and Marginalia has no public API. The MLR therefore searches (a) the free APIs of the large practitioner sites (Stack Exchange, GitHub, Hacker News; dev.to was dropped because its public API has no search), (b) grey literature in free open indexes (OpenAlex and Zenodo: reports, theses, white papers, documentation) and (c) the links of the sources already included (backlink snowballing, G7). The option of adding one's own Brave Search API key was dropped in October 2026: keeping the raw answers and the sources found, as this review does, would break Brave's terms whoever's key is used. The protocol states which searches were used, and that no general web search engine was used and why.
2. **The archive: the middle way.** The text of each web page is kept on the server for the run's lifetime, so the citation check can be repeated. The archive holds the URL, the access date, the SHA-256 of the text, the quoted passages and a Wayback Machine link, not other people's pages.
3. **Videos and talks: only with a published transcript.** A video counts only when its transcript is published with it; quotes are checked against that transcript. Videos without one are left out, and the protocol says so.
4. **The quality threshold: a setting in points.** Each grey source is scored out of 20 points on the criteria of Table 7, and the reviewer sets the threshold in the planning form (default 10 of 20, as in the paper's example). Points are easier to read than a fraction; the value goes into the protocol.
5. **Contacting people (G7): not done.** The planning form takes no sources by hand; only sources found by the searches are reviewed, and the protocol states that practitioners and authors were not contacted.

## 6. What not to change

The rules in `AGENTS.md` hold for the MLR path too: model answers are validated in code, every value from a source needs a quote found in that source's text, the method is described by code and not by the model, and every decision is on file. Grey literature makes these rules more important, not less, because its sources are less controlled.
