# TraceableAI developer wiki

This wiki explains how the code of TraceableAI fits together, for someone who has to change it without having written it. It reads best in order: start with the system overview, follow a single review through the pipeline, and then go into whichever part you need to change. Every page names the files and methods it describes, so you can keep the code open next to it.

These pages live in `docs/wiki/` in the repository and are copied to the repository's Wiki tab automatically (`.github/workflows/wiki.yml`) whenever they change on master, so edit them here, not in the Wiki tab.

The wiki describes the design as it is in the repository. When you change how something works, change the page that describes it in the same pull request; a wiki that has drifted away from the code is worse than none.

| Page | What it answers |
|---|---|
| [1. System overview](01-system-overview.md) | What the parts are, how a browser request reaches the engine, and where state lives |
| [2. The review pipeline](02-review-pipeline.md) | What happens, in which order, from "Start review" to a finished report |
| [3. Search and screening](03-search-and-screening.md) | How sources are queried, how records are de-duplicated, screened twice, chained and reviewed by a human |
| [4. Evidence and report](04-evidence-and-report.md) | How full text is fetched, how studies are extracted and appraised, how the report is written and its citations checked |
| [5. Ledger, archive and data model](05-ledger-and-archive.md) | What is saved for each run, the main data types, and how the downloadable archive and `main.tex` are built |
| [6. Web app, operations and deployment](06-web-app-and-operations.md) | Pages and endpoints, quotas, concurrency, caching, clean-up, configuration, security and CI/CD |
| [7. Extending and testing](07-extending-and-testing.md) | How to add a source, change a prompt or add a stage without breaking reproducibility, and how the tests are organised |
| [8. Setup and configuration](08-setup-and-configuration.md) | Keys, local models, Docker, every setting with its default, several users at once, and what the checks can and cannot catch |

## The project in one paragraph

TraceableAI is a .NET 10 Blazor Server application. A user enters a research question and eligibility criteria; the app writes a review protocol, searches open scholarly databases, screens every record twice with a language model, follows the citations of the included studies, extracts and appraises each study, codes their findings into themes, writes a PRISMA 2020 report with one cited subsection per theme, and checks every cited sentence against a verbatim quote from the paper it cites, repairing the ones that fail. Everything the model decides is written to a per-run ledger, and the whole run can be downloaded as an archive whose files are fingerprinted in a manifest. Almost all of the logic lives in one class, `PrismaReviewEngine`, split into partial files that sit in the folder of the stage they belong to; the rest are small, single-purpose helpers around it.

## Where the code is

The source is grouped by pipeline stage, so the folders read in the same order as a review runs. [ARCHITECTURE.md](https://github.com/lauPhilip/au-btech-literature-review-agent/blob/master/ARCHITECTURE.md) at the repository root explains the layout, and every folder has a short README of its own.

```
AuBtechReviewAgent/                 the web app
  Program.cs                        start-up: configuration, services, endpoints
  Pipeline/                         the engine core: one run from start to finish, models, progress, cache
  Sources/                          one class per bibliographic database, and how they are called
  Search/                           search strings, de-duplication, saturation
  Screening/                        two independent screenings, exclusion reasons, the human review
  Evidence/                         full texts, data extraction, quality appraisal
  Synthesis/                        themes, grounded writing, the artifact
  Verification/                     citation range and support checks, repair, re-checks
  Report/                           methods text, PRISMA flow, references, archive and manifest
  Runs/                             what happens after a run: ownership, notes, metrics, clean-up
  Llm/                              model access, JSON answers, prompt safety, the call ledger
  Web/                              site pages' helpers: SEO, health, security headers, quota, glossary
  Components/Pages/*.razor          the routable pages (landing, dashboard, review output, ...)
  Components/Dashboard/*.razor      parts of the dashboard (funnel, progress, run summary, ...)
  Components/Layout/*.razor         header, footer, SEO tags, help terms
  wwwroot/                          static files (compiled Tailwind CSS, scripts, images)
AuBtechReviewAgent.Tests/           xUnit tests, all offline, in the same folders as the code they test
tools/ScreeningEval/                command-line tool: screening accuracy against a labelled dataset
deploy/app_offline.htm              the page shown while a new version is uploaded
.github/workflows/                  CI, CodeQL, release, wiki sync
docs/wiki/                          this wiki
```
