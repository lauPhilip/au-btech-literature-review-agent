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

A review goes through eight stages, and every stage writes to the Research Ledger as it runs rather than only at the end.

```mermaid
flowchart LR
    Q[Query + criteria] --> S1[1 Multi-perspective retrieval]
    S1 --> S2[2 Screening]
    S2 --> S3[3 Evidence extraction]
    S3 --> S4[4 Grounded outline]
    S4 --> S5[5 Cited synthesis]
    S5 --> S6[6 Citation validation]
    S6 --> S7[7 Automated peer review]
    S7 --> S8[8 Stylistic refinement]
    S8 --> OUT[PRISMA report + run archive]
    S1 & S2 & S3 & S4 & S5 & S6 & S7 & S8 -.-> L[(Research Ledger)]
```

The run starts by asking the model for a few extra phrasings of your query (a wording variant, a narrower sub-topic, and a method-focused version) and searches each source you ticked in the dashboard (arXiv, Scopus/ScienceDirect, IEEE Xplore, Google Scholar and ResearchGate) with each of them, removing duplicates by identifier and title. Sources that need an API key are greyed out until one is available. A per-source result cap sets a hard upper limit no matter how many phrasings run, which keeps test runs small, cheap and repeatable, and records outside the chosen publication years are dropped. Each candidate is then screened by the language model against your inclusion and exclusion criteria, with an optional peer-reviewed-only filter. The reference for each paper is built directly from the metadata the source returned rather than written by the model, and journals are given a quartile only when their title matches the Scimago dataset exactly.

The PDFs of the papers that pass are downloaded, and their text is extracted page by page with PdfPig and split into short overlapping chunks, each tagged with the paper it came from. Before any prose is written, the model builds a grounded outline that maps each claim to the reference numbers that support it, and the results and discussion are then written against that outline so that every specific claim ends with an inline citation.

The methods sections of the report (eligibility, information sources, search strategy and selection process) are not written by the model at all. They are generated from the run's own settings and ledger, so they list exactly the sources and search strings that were used and never describe a step that did not happen.

Three checks follow. A citation validator removes any reference number that falls outside the reference list (citing [56] when there are only 53 sources) and logs each removal. An automated peer-review pass has one model critique the synthesis for depth, citation coverage, grounding and over-claiming, and a second model revise it; the comments and the before-and-after text are kept. Finally, a stylistic pass tightens the wording of the abstract, rationale and objectives and records every rewrite next to the original. A rewrite that changes a number, drops a citation or grows far beyond a copy-edit is rejected and the original is kept, and the ledger says why.

## What you get after a run

Each run clears out the previous one and leaves a single .zip you can download. Every file in it documents a different part of the process.

```
├── SourcePapers/                          # The exact PDFs used in this run
├── main.tex                               # The finished review as a LaTeX document
├── prisma-report.json                     # The PRISMA 2020 checklist items
├── transparent-process.json               # The Research Ledger: searches, screening decisions, reasoning
├── grounded-outline.txt                   # The claim-to-source outline written before the prose
├── citation-audit.json                    # Out-of-range citation markers that were removed
├── peer-review-feedback.json              # Reviewer comments plus before/after text
└── stylistic-transformation-ledger.json   # Every stylistic rewrite, with the original text
```

| File | What it lets you check |
|---|---|
| `transparent-process.json` | Which query hit which source and when, what was found, and why each paper was included or excluded |
| `grounded-outline.txt` | Which source each claim was assigned to before any prose was written |
| `citation-audit.json` | Which invalid reference numbers the validator removed |
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

The citation validator only checks that each reference number exists in the list. The numbers the model sees, the numbers it writes and the numbers in the bibliography all come from one ordering, so a valid [5] always points at the fifth entry; but a citation that points to a real entry that does not actually support the claim still passes unchanged. References are now built from source metadata, so they are only as good as what the database returned: a missing year shows as "n.d." and a missing DOI is left out rather than invented. The automated peer reviewer catches some unsupported citations; in our pilot study it caught five of seven hallucinated citations, leaving two for the human check. Screening rationales are consistent and easy to audit, but they are also formulaic, and they are no substitute for a reviewer reading the papers.

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

## Tests and continuous integration

The project has an xUnit test suite covering citation validation, reference building, the generated methods text, the stylistic-pass guard, the run quota, the LaTeX output, input sanitizing and the journal-ranking lookup. Run it from the repository root with:

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
