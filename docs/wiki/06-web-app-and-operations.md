# 6. Web app, operations and deployment

The pipeline on pages 2 to 5 runs inside an ordinary ASP.NET Core web app. This page covers the parts around it: start-up, pages and endpoints, the limits that keep the app affordable and responsive, configuration, security, and how a change reaches the live site.

## Start-up (`Program.cs`)

`Program.cs` reads configuration, creates the shared services, and sets up the request pipeline, in that order.

The configuration sections are bound to option classes: `Quota` → `QuotaOptions`, `Runs` → `RunsOptions`, `Llm` → `LlmOptions`, `Cache` → `CacheOptions`, `OpenSources` → `OpenSourcesOptions`, `Synthesis` → `SynthesisOptions` (thematic synthesis on or off, dual coding, citation repair, second citation check, maximum number of themes, studies per coding call), `Metrics` → `MetricsOptions` (metrics store on or off, its folder), plus `Report:SupportStatement` and the API keys (`MISTRAL_API_KEY`, `ELSEVIER_API_KEY`, `IEEE_API_KEY`). The quota service, cache, run options and review engine are created once and registered as singletons; `UiStateContainer` is scoped, so each browser session gets its own. `SessionCleanupWorker` is registered as a hosted background service. Before the app starts serving, `MarkInterruptedRuns` marks runs that were cut off by the previous shutdown.

The request pipeline then adds, in order: forwarded headers (for a reverse proxy, if one is configured), the security headers on every response, the error page and HSTS outside development, HTTPS redirection, static files, antiforgery, the rate limiter, the three download endpoints, and finally the Razor components in interactive-server mode.

## Pages, components and endpoints

| Route or name | File | Purpose |
|---|---|---|
| `/` | `Components/Pages/Landing.razor` | Public landing page |
| `/review`, `/review/{runId}` | `Components/Pages/Home.razor` | Dashboard: API keys and quota, the review form with its input check, run status, live progress, PRISMA funnel, JSON view, the human screening review |
| component | `RunProgressPanel.razor` | The progress bar, current step with detail, elapsed time and the list of steps |
| `/spec-matrix`, `/spec-matrix/{runId}` | `Components/Pages/SpecMatrix.razor` | Review Output: the report with clickable citations, tables, charts, downloads, "Delete run" |
| `/metrics` | `Components/Pages/Metrics.razor` | Run quality metrics across runs: verdict shares per run, groups by app version and settings, and where a run's citations fail. Shown only with the developer token (`Quota:AdminToken`) or in Development, because it lists every run on the deployment |
| component | `ScreeningReviewPanel.razor` | The include/exclude list shown while a run waits for review |
| component | `PrismaFunnel.razor` | The funnel on the dashboard, with "show" links for removed records |
| `GET /api/workspace/{id}/archive` | `Program.cs` | The run archive as a zip |
| `GET /api/workspace/{id}/references.bib` / `.ris` | `Program.cs` | The included studies for reference managers |
| `GET /api/workspace/{id}/protocol.md` | `Program.cs` | The protocol, linked from Review Output |

The dashboard subscribes to `ReviewEngine.OnProgressUpdated` while it is open and unsubscribes when it is disposed. Progress is set by the engine through `ReportProgress` (`RunProgress.cs`): the run's step plan (`ProgressPlan`, with citation chaining and the human review only when asked for), the current step, how far it is from 0 to 1 and a short detail such as "16 of 40 records screened" are kept in `ReviewStats`. `RunProgress.Percent` turns them into an overall percentage using a rough weight per step, and within a step the value never goes back. Updates inside a step are sent at most every 400 ms and do not write the ledger, so loops can report freely. The Review Output page does not follow a run live; it reads the finished files from the run folder each time it loads. Anyone with a run's link can open it, which is why run ids are random GUIDs and runs are deleted after `Runs:RetentionDays`.

## Limits: quota, slots, cache

Three mechanisms keep the app affordable and responsive, and they are independent of each other.

`RunQuotaService` decides **whether a run may start**. It has three tiers: *Free* (the server's Mistral key; `Quota:FreeRunsPerDay` per client address, `Quota:GlobalFreeRunsPerDay` across everyone, and at most `Quota:FreeTierMaxResults` results per source), *OwnKey* (the visitor's own Mistral key; `Quota:OwnKeyRunsPerHour`), and *Developer* (a valid `Quota:AdminToken`, no limits). In development every run is unlimited when `Quota:UnlimitedInDevelopment` is true. Counters are stored in `App_Data/run-quota.json`. A run reserves its place with `TryReserve` and gets a `QuotaLease`, which is refunded if the run never starts.

`RunCoordinator` decides **when a started run may use the model**. At most `Runs:MaxConcurrentRuns` runs hold a slot at the same time; the rest queue, and their dashboards show the position. It also holds the gates for runs waiting on a human screening review. Everything in it is in memory.

`ReviewCache` decides **whether work can be reused**. Search responses are kept for `Cache:SearchResponseHours` (24) and screening decisions for `Cache:ScreeningDecisionDays` (90), in `App_Data/cache/`. The keys include everything the result depends on, so a changed prompt, model or criterion never reuses an old answer. Reuse is marked per record in the ledger.

Separately, the download endpoints are rate-limited per client address (20 per minute, policy `ArchiveDownloadPolicy`). Runs themselves start over the SignalR connection, which HTTP rate limiting never sees; that is what the quota service is for.

## Clean-up

`SessionCleanupWorker` runs every hour and deletes run folders whose newest file is older than `Runs:RetentionDays`, skipping runs that are still active. It looks at the newest file inside the folder rather than the folder's own date, because the folder date does not change when the ledger inside is rewritten. In the same pass, `ReviewCache.Prune` removes expired cache files.

## Configuration and secrets

| Setting | Where it comes from locally | Where it comes from on Simply.com |
|---|---|---|
| Non-secret defaults | `appsettings.json` | `appsettings.json` (uploaded with the app) |
| API keys, `Quota:AdminToken`, `OpenSources:SemanticScholarApiKey` | `dotnet user-secrets` | Environment variables in the server's `web.config` (`OpenSources__SemanticScholarApiKey`; `__` is the section separator) |

Secrets are never committed. `appsettings.json` holds only placeholders, and the server's `web.config` is excluded from every deploy so the keys on the server are never overwritten.

## Security measures

`SecurityHeaders.Apply` adds a strict Content Security Policy (scripts and styles only from the site itself, images from the site and `data:`), `X-Frame-Options: DENY`, `nosniff`, a referrer policy and a permissions policy to every response. All scripts are served locally: Tailwind is compiled ahead of time into `wwwroot/css/tailwind.css`, and Mermaid is vendored under `wwwroot/lib/mermaid`. User-provided API keys are cleaned with `SecurityUtility.SanitizeApiKey` (only letters, digits and `. _ - :` survive; anything else empties the field) and kept in the browser's protected session storage, not on the server.

The text a reviewer types into the configuration panel goes into prompts, so it is checked by `ReviewInputGuard` before a run starts, both in the dashboard and again at the top of `RunReviewAsync`. The guard normalises every field (Unicode NFKC; invisible and control characters such as zero-width spaces and bidirectional overrides removed; the markers the prompts use for third-party text defused), enforces a length limit per field, and scans for instruction-like phrases with the same patterns `PromptSafety` uses for paper text, minus the two that are ordinary in eligibility criteria ("should be included if ..."). Too-long or missing fields are errors; flagged phrases are shown to the reviewer, who can start the run anyway, and are then listed in `protocol.md` and in `ReviewState.InputFlags`. Nothing the reviewer typed is rewritten silently; the earlier `SanitizeInput` replaced phrases and stripped anything between `<` and `>`, which also removed text such as "< 5 years". Every prompt that contains the reviewer's fields carries `PromptSafety.ReviewerInputNotice`, and every structured answer is still validated in code, which is what bounds what a prompt can make the model do. Log messages go through `SanitizeLogMessage`, which removes line breaks (against log forging) and anything that looks like an API key. Run folder paths are built only by `GetWorkspaceFolderPath`, which checks that they stay inside the workspace.

## Deployment

```mermaid
flowchart LR
    PUSH["push / merged PR<br/>to master"] --> BT["Build & Test<br/>dotnet test"]
    PUSH --> CSS["Compiled CSS matches<br/>the components"]
    PUSH --> CQ["CodeQL<br/>(separate workflow)"]
    BT & CSS --> DEP{"DEPLOY_ENABLED?"}
    DEP -- yes --> PUB["dotnet publish<br/>win-x86, self-contained"]
    PUB --> OFF["upload app_offline.htm"]
    OFF --> UP["upload files over FTPS<br/>(not web.config, App_Data,<br/>WorkspaceStore)"]
    UP --> ON["delete app_offline.htm<br/>→ new version starts"]
```

The site runs on Simply.com's Windows hosting under IIS. That hosting only has .NET Framework installed, so the app is published **self-contained for win-x86** and runs **out of process** (`AspNetCoreHostingModel` in the project file): with in-process hosting, IIS returned empty static files from the host's network share.

`.github/workflows/ci.yml` does the deployment. On every push to master, once *Build & Test* and the CSS check pass and the repository variable `DEPLOY_ENABLED` is `true`, the deploy job publishes the app and uploads it with `lftp` over FTPS, using the secrets `SIMPLY_FTP_USER` and `SIMPLY_FTP_PASSWORD`. It first uploads `deploy/app_offline.htm`, which makes IIS stop the app and show that page for every request, then mirrors the new files (never deleting anything on the server and never touching `web.config`, `App_Data` or `WorkspaceStore`), and finally deletes `app_offline.htm`, which starts the new version. A deploy takes about six minutes, most of it the upload. If it fails halfway, the site keeps showing the update page rather than running a half-updated app; re-run the job or delete the file by FTP.

The CSS check exists because Tailwind only includes classes that literally appear in the components, including words in ordinary text. If you change a component, run `npm run build:css` in `AuBtechReviewAgent` and commit `wwwroot/css/tailwind.css` with it. `codeql.yml` scans C# and JavaScript on every push; `release.yml` runs when you push a version tag such as `v1.2.0` and creates a GitHub release, which Zenodo archives when that integration is switched on; Dependabot proposes dependency updates, which pass through the same checks.
