# TraceableAI

A tool for running systematic literature reviews (SLRs) that you can check afterwards. It follows the PRISMA 2020 reporting standard and keeps a record of every step, so you can trace any statement in the final report back to the paper it came from.

Built at the Department of Business Development and Technology (BTECH), Aarhus University, and presented at OSSYM 2026, the 8th International Open Search Symposium.

[![CI/CD](https://github.com/lauPhilip/au-btech-literature-review-agent/actions/workflows/ci.yml/badge.svg)](https://github.com/lauPhilip/au-btech-literature-review-agent/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![LLM Engine](https://img.shields.io/badge/Engine-Mistral%20Large-purple.svg)](https://mistral.ai/)
[![Reporting](https://img.shields.io/badge/Reporting-PRISMA%202020-green.svg)](https://prisma-statement.org/)
[![License](https://img.shields.io/badge/License-Apache%202.0-yellowgreen.svg)](LICENSE)
[![OSSYM](https://img.shields.io/badge/OSSYM-2026-003B5C.svg)](https://opensearchfoundation.org/)

## What it's for

Most tools that write literature reviews for you have the same problem: the text reads well, but the citations behind it are often wrong, mismatched, or made up. That makes the output hard to trust, and it can't be used for a review that has to follow PRISMA 2020, where you're expected to show how you got to your conclusions.

TraceableAI is an attempt to fix that. Instead of just handing you a finished write-up, it saves a full record of what it searched for, which papers it kept or dropped and why, and which source each statement in the report is based on. We call that record the Research Ledger. If something looks off, you can go back and check it. The tool is meant as a co-pilot for a human reviewer, not a replacement: the ledger exists so that a person can verify the work.

## How a run works

A review goes through eight stages, plus an optional pause where you check the screening decisions yourself. Every stage writes to the Research Ledger as it runs rather than only at the end.

```mermaid
flowchart LR
    Q[Query + criteria] --> S1[1 Multi-perspective retrieval]
    S1 --> S2[2 Screening]
    S2 -.-> H[Optional: your screening review]
    H -.-> S3
    S2 --> S3[3 Evidence extraction]
    S3 --> S4[4 Grounded outline]
    S4 --> S5[5 Cited synthesis]
    S5 --> S6[6 Citation checks]
    S6 --> S7[7 Automated peer review]
    S7 --> S8[8 Stylistic refinement]
    S8 --> OUT[PRISMA report + run archive]
    S1 & S2 & S3 & S4 & S5 & S6 & S7 & S8 -.-> L[(Research Ledger)]
```

The run starts by asking the model for a few extra phrasings of your query (a wording variant, a narrower sub-topic, and a method-focused version) and searches each source you ticked in the dashboard (arXiv, Scopus/ScienceDirect, IEEE Xplore, Google Scholar and ResearchGate) with each of them, removing duplicates by identifier and title. Sources that need an API key are greyed out until one is available. A per-source result cap sets a hard upper limit no matter how many phrasings run, which keeps test runs small, cheap and repeatable, and records outside the chosen publication years are dropped. Each candidate is then screened by the language model against your inclusion and exclusion criteria, with an optional peer-reviewed-only filter. The reference for each paper is built directly from the metadata the source returned rather than written by the model, and journals are given a quartile only when their title matches the Scimago dataset exactly.

If you tick "Let me check the screening decisions before the write-up", the run pauses here. The dashboard lists every screened paper with the model's decision, its reasoning and the abstract, and you can include or exclude papers and add a note before the run continues. Each decision you confirm or change is stored in the ledger next to the model's original decision, and the methods section and flow diagram report how many you checked and changed.

The PDFs of the papers that pass are downloaded, and their text is extracted page by page with PdfPig and split into short overlapping chunks, each tagged with the paper it came from. Every included paper is represented in the material the model writes from: its abstract plus an equal share of its most relevant full-text passages, so one long PDF can no longer crowd out the others. Before any prose is written, the model builds a grounded outline that maps each claim to the reference numbers that support it, and the results and discussion are then written against that outline so that every specific claim ends with an inline citation.

The methods sections of the report (eligibility, information sources, search strategy and selection process) are not written by the model at all. They are generated from the run's own settings and ledger, so they list exactly the sources and search strings that were used and never describe a step that did not happen.

Three checks follow. A citation validator removes any reference number that falls outside the reference list (citing [56] when there are only 53 sources) and logs each removal. A citation support check then asks, for every cited sentence, whether the cited paper actually says what the sentence claims: the model gets the sentence and the most relevant excerpts of that paper and must answer with a verdict and a word-for-word quote, and the quote is checked against the excerpt in code, so a "supported" verdict with an invented quote is downgraded to "could not be verified". Nothing is deleted from the report; the verdicts go into `citation-audit.json` and a one-paragraph summary goes into the report. An automated peer-review pass has one model critique the synthesis for depth, citation coverage, grounding and over-claiming, and a second model revise it; the comments and the before-and-after text are kept. Finally, a stylistic pass tightens the wording of the abstract, rationale and objectives and records every rewrite next to the original. A rewrite that changes a number, drops a citation or grows far beyond a copy-edit is rejected and the original is kept, and the ledger says why.

## What you get after a run

Each run has its own link (`/review/{run-id}`), so you can reload the page, bookmark it or open it on another device, and results are kept for seven days. The Review Output page has a single .zip download plus BibTeX and RIS files for Zotero, EndNote or Mendeley. Every file in the .zip documents a different part of the process.

```
├── SourcePapers/                          # The exact PDFs used in this run
├── main.tex                               # The finished review as a LaTeX document, with a PRISMA flow diagram
├── references.bib / references.ris        # The included papers for your reference manager
├── prisma-report.json                     # The PRISMA 2020 checklist items
├── transparent-process.json               # The Research Ledger: searches, screening decisions, reasoning
├── grounded-outline.txt                   # The claim-to-source outline written before the prose
├── citation-audit.json                    # Removed out-of-range citations and the citation support verdicts
├── llm-calls.json                         # Every model call: stage, model, temperature, prompt hash, tokens
├── peer-review-feedback.json              # Reviewer comments plus before/after text
└── stylistic-transformation-ledger.json   # Every stylistic rewrite, with the original text
```

| File | What it lets you check |
|---|---|
| `transparent-process.json` | Which query hit which source and when, what was found, and why each paper was included or excluded |
| `grounded-outline.txt` | Which source each claim was assigned to before any prose was written |
| `citation-audit.json` | Which invalid reference numbers the validator removed, and for every cited sentence whether the cited paper supports it, with the quoted evidence |
| `llm-calls.json` | Exactly how the run was produced: model and app version, temperature per stage, retries, token use, and a SHA-256 fingerprint of every prompt, so two runs can be compared |
| `peer-review-feedback.json` | What the automated reviewer criticised and whether the revision was applied |
| `stylistic-transformation-ledger.json` | That stylistic rewriting didn't change facts, by comparing each rewrite with its original |
| `prisma-report.json` / `main.tex` | The finished review itself |

A screening decision in the ledger looks like this (abridged from a real run on the default query):

```json
{
  "PaperId": "http://arxiv.org/abs/2607.02703v1",
  "Title": "LLMoxie: Exploring Agentic AI for Scientific Software Development",
  "Decision": "Included",
  "Reasoning": "The paper focuses on agent architecture (LLMoxie's three-tiered AI platform and Plugin-Agent-Skill hierarchy) and loop execution (six-phase research-and-implement workflow), meeting the inclusion thresholds. It does not pertain to agronomy or commercial marketing, avoiding exclusion criteria."
}
```

## What the checks do and don't catch

The safeguards reduce the problem of unreliable citations but do not remove it, and it is worth being precise about where the limits are.

The citation validator only checks that each reference number exists in the list. The numbers the model sees, the numbers it writes and the numbers in the bibliography all come from one ordering, so a valid [5] always points at the fifth entry. Whether that paper really supports the sentence is judged by the citation support check, which is itself a model judgement: it only sees the most relevant excerpts, and for papers without a downloadable full text (currently everything except arXiv) it only sees the abstract, so "not supported" can mean "not in the abstract". Treat its verdicts as a list of places to look first, not as proof. References are now built from source metadata, so they are only as good as what the database returned: a missing year shows as "n.d." and a missing DOI is left out rather than invented. The automated peer reviewer catches some unsupported citations; in our pilot study it caught five of seven hallucinated citations, leaving two for the human check. Screening rationales are consistent and easy to audit, but they are also formulaic, and they are no substitute for a reviewer reading the papers.

The ledger is what makes these errors findable. Before you use any output, follow a sample of claims from the report back through the outline and the ledger to the source PDFs.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and a Mistral API key. Check the SDK with `dotnet --version`, which should print a version starting with `10.`.

Store the API key with .NET user secrets so it never ends up in git:

```
cd AuBtechReviewAgent
dotnet user-secrets set "MISTRAL_API_KEY" "your-key-here"
```

The other sources are optional and use the same pattern with `ELSEVIER_API_KEY`, `IEEE_API_KEY` and `SCHOLAR_API_KEY` (a SerpApi key, which covers both Google Scholar and ResearchGate). arXiv needs no key.

Then start the app with hot reload:

```
dotnet watch
```

NuGet packages are restored automatically on the first build, so no separate install step is needed. The terminal shows the local address the app is listening on.

## Several users at once

Each run gets its own ID, its own folder under `WorkspaceStore/` and its own link, so users never see or overwrite each other's runs. To stay within Mistral's rate limits, at most three runs (`Runs:MaxConcurrentRuns`) call the model at the same time; further runs wait in line and the dashboard shows their place. Rate-limit and temporary server errors from Mistral are retried with a growing delay before a run gives up. Finished runs are deleted after `Runs:RetentionDays` (7) days, counted from the last change to any file in the run, and a run that is still going is never deleted.

A run lives in the app's memory while it is working, so it cannot survive an app restart. When the app starts, any run that was cut off is marked "Interrupted" and its link says so. When deploying to Simply (IIS), set the application pool's idle time-out to 0 (or as high as allowed) and move the scheduled recycle to a quiet hour, and switch on WebSockets for the site; without WebSockets the dashboard falls back to a slower and less reliable connection.

## Usage limits

A public deployment runs on the server's own Mistral key, so each visitor gets a small daily quota instead of having to bring a key. The limit is counted per client address per UTC day and is stored on disk, so a restart does not reset it. Addresses are never written down: each one is replaced by a keyed hash that is enough to count runs but cannot be turned back into an IP address. Visitors who add their own Mistral key under API Credentials are not bound by the daily limit, only by a light hourly cap, and a developer access token removes the limit altogether.

| Setting (in `appsettings.json` or user secrets) | Default | What it does |
|---|---|---|
| `Quota:FreeRunsPerDay` | 3 | Runs per client address per day on the server key |
| `Quota:GlobalFreeRunsPerDay` | 50 | Ceiling on server-key runs per day across everyone, which is what actually caps the bill |
| `Quota:OwnKeyRunsPerHour` | 10 | Hourly cap for visitors using their own Mistral key |
| `Quota:FreeTierMaxResults` | 5 | Highest "max results per source" on the free tier |
| `Quota:AdminToken` | empty | Developer access token; set it with `dotnet user-secrets set "Quota:AdminToken" "..."`, never in git |
| `Quota:UnlimitedInDevelopment` | true | No limit while running locally in the Development environment |
| `ReverseProxy:KnownProxies` | empty | Only needed if a separate proxy sits in front of the app and sends `X-Forwarded-For` |
| `Report:SupportStatement` | empty | The funding and support text printed in every report |
| `Runs:MaxConcurrentRuns` | 3 | Runs allowed to call the model at the same time; the rest wait in line |
| `Runs:RetentionDays` | 7 | How long a run's results and link are kept |
| `Runs:ScreeningReviewTimeoutHours` | 24 | How long a run waits for your screening review before continuing with the model's decisions |

## Measuring screening quality

`tools/ScreeningEval` runs the pipeline's own screening prompt over a dataset whose inclusion decisions were made by human reviewers and reports recall, precision, specificity, F1 and Cohen's kappa, together with every relevant paper the model missed. It reads the ASReview CSV format (`title`, `abstract`, `label_included`), which is what the [SYNERGY datasets](https://github.com/asreview/synergy-dataset) of 26 published systematic reviews use. Take the inclusion and exclusion criteria from the original review so the comparison is fair.

```
$env:MISTRAL_API_KEY="your-key-here"
dotnet run --project tools/ScreeningEval -- --data review.csv --inclusion "..." --exclusion "..." --max-excluded 200
```

Relevant papers are rare in real screening data, so `--max-excluded` keeps every relevant paper and a fixed random sample of the others (the same sample every time for a given `--seed`). Recall is then exact, and precision is also estimated for the full dataset. Results and the model's reasoning for every record are written to `eval-results.json`.

## Tests and continuous integration

The project has an xUnit test suite covering citation validation and the citation support check, reference building and BibTeX/RIS export, the generated methods text and flow diagram, the stylistic-pass guard, the run quota, the run queue and screening review, retention, the LaTeX output, the evaluation metrics, input sanitizing and the journal-ranking lookup. It also runs a complete review offline against a scripted model and source, including the screening-review pause. Run it from the repository root with:

```
dotnet test
```

On every push and pull request, GitHub Actions builds the project and runs the tests (see the CI/CD badge above).

Deployment to the Simply server is also set up in the same workflow, but it stays switched off until you enable it: set a repository variable `DEPLOY_ENABLED` to `true` and add the server connection details as repository secrets. Until then, the deploy step is skipped and only the build-and-test step runs. The workflow file (`.github/workflows/ci.yml`) explains exactly which secrets to add and where to fill in the deploy command for your setup.

## Citing TraceableAI

If you use TraceableAI in your research, please cite the OSSYM 2026 paper:

```bibtex
@inproceedings{lau2026traceableai,
  author    = {Lau, Philip S. P. {\O}. O. and Nidhi},
  title     = {{TraceableAI}: An Open-Source Agentic Framework for {PRISMA}-Compliant Literature Synthesis},
  booktitle = {Proceedings of the 8th International Open Search Symposium (OSSYM 2026)},
  year      = {2026}
}
```

## Contributing

This is an open-source project and contributions are welcome, whether that's reporting a bug, suggesting a feature, fixing a typo, or writing code. Have a look at [CONTRIBUTING.md](CONTRIBUTING.md) for how to set the project up locally and how to send changes. By taking part, you agree to follow our [Code of Conduct](CODE_OF_CONDUCT.md). If you're not sure where to start, open an issue and ask; we're happy to help.

## License

TraceableAI is released under the [Apache License 2.0](LICENSE). In short, you're free to use, change, and redistribute it, including for your own projects, as long as you keep the license and copyright notice. See the [LICENSE](LICENSE) and [NOTICE](NOTICE) files for the full terms.
