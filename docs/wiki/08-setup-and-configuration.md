# 8. Setup and configuration

This page has the details the README leaves out: API keys and contact address, running with a local model, Docker, the Tailwind build, every setting with its default, how several users share a deployment, and what the checks can and cannot catch.

## Keys and contact address

You need the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (`dotnet --version` should start with `10.`) and either a Mistral API key or a local model. Keys go into .NET user secrets, so they never end up in git:

```
cd AuBtechReviewAgent
dotnet user-secrets set "MISTRAL_API_KEY" "your-key-here"
dotnet user-secrets set "OpenSources:ContactEmail" "you@example.org"
```

OpenAlex, Crossref and Unpaywall ask callers to identify themselves with an e-mail address, and Unpaywall does not answer without one. Scopus/ScienceDirect and IEEE Xplore are optional and use the same pattern with `ELSEVIER_API_KEY` and `IEEE_API_KEY`. arXiv, Semantic Scholar and Crossref need no key; a free Semantic Scholar key (`OpenSources:SemanticScholarApiKey`) only raises its rate limit. OpenAlex has expected a key since February 2026: without one its daily budget is tiny and shared by everyone on the same address, so most OpenAlex queries and citation-chaining lookups are refused with 429. Get a free key at openalex.org/settings/api and set it with `dotnet user-secrets set "OpenSources:OpenAlexApiKey" "<key>"` (on the server: `OpenSources__OpenAlexApiKey`). It is sent as an `Authorization` header, never in an address, so it stays out of the logs and the archive. Start the app with `dotnet watch`; NuGet packages are restored on the first build, and the terminal shows the address.

On the server, the same settings are environment variables in `web.config`, with a double underscore between section and name (`OpenSources__ContactEmail`, `Quota__AdminToken`).

## Running with a local model

Any server that speaks the OpenAI chat completions API can replace Mistral, including a local one, so nothing is sent to a commercial provider. With [Ollama](https://ollama.com) running `llama3.1`:

```
dotnet user-secrets set "Llm:Provider" "OpenAICompatible"
dotnet user-secrets set "Llm:Model" "llama3.1"
dotnet user-secrets set "Llm:BaseUrl" "http://localhost:11434/v1"
```

`Llm:ApiKey` is only needed for hosted OpenAI-compatible services. The model name is written into the methods section and `llm-calls.json`. Smaller local models follow the JSON instructions less reliably, so expect more repaired answers and failed screenings than with Mistral Large.

## Docker

The main deployment is the .NET app on Simply, which does not use Docker. For running elsewhere, the `Dockerfile` builds an image (not part of any release so far):

```
docker build --build-arg APP_VERSION=$(git describe --tags) -t traceableai .
docker run -p 8080:8080 -e MISTRAL_API_KEY=... -e OpenSources__ContactEmail=you@example.org \
  -v traceable-runs:/app/WorkspaceStore -v traceable-data:/app/App_Data traceableai
```

The two volumes keep runs, run quotas, the cache and the metrics store when the container is replaced.

The image is built without the git history, so it cannot read its version from the tag the way other builds do (page 6, Versions). `APP_VERSION` passes it in; without it the app calls itself `0.0.0-local`. A build on your own machine needs no argument, as long as the clone has its tags: a clone made with `--depth 1` gets a wrong version, so run `git fetch --tags --unshallow` first.

## Styling

Tailwind CSS is compiled ahead of time into `wwwroot/css/tailwind.css`, which is committed, so Node.js is only needed when you change the classes a component uses. Then run `npm ci` once and `npm run build:css` (or `npm run watch:css` while editing) in `AuBtechReviewAgent`, and commit the result; CI fails when it is out of date.

## Every setting

All of these can be set in `appsettings.json` (non-secret values only), user secrets or environment variables.

| Setting | Default | What it does |
|---|---|---|
| `Quota:FreeRunsPerDay` | 3 | Runs per client address per day on the server key |
| `Quota:GlobalFreeRunsPerDay` | 50 | Ceiling on server-key runs per day across everyone |
| `Quota:OwnKeyRunsPerHour` | 10 | Hourly cap for visitors using their own Mistral key |
| `Quota:FreeTierMaxResults` | 5 | Highest "max results per source" on the free tier |
| `Quota:AdminToken` | empty | Developer access token (secret); removes limits and opens the Metrics page |
| `Quota:UnlimitedInDevelopment` | true | No limit, and Metrics page open, in the Development environment |
| `ReverseProxy:KnownProxies` | empty | Only needed when a separate proxy sends `X-Forwarded-For` |
| `Report:SupportStatement` | empty | Funding and support text printed in every report |
| `Runs:MaxConcurrentRuns` | 3 | Runs allowed to call the model at the same time; the rest wait in line |
| `Runs:RetentionDays` | 7 | How long a run's results and link are kept |
| `ReviewModules:Preview` | (empty) | Modules shown as a preview while they are being built, e.g. `["multivocal"]`: their pages open, their cards get a "Preview" chip, and the model card and privacy page describe them (`ReviewModulesOptions.Shows`). Read only in Development; ignored everywhere else. In production a review is opened and described only once its card is switched on (`Available: true` in its module), which is the one switch that makes it public |
| `Runs:DemoRunId` | (empty) | A finished run shown on the landing page as "See an example report"; kept and read-only |
| `Site:PublicUrl` | `https://au-btech-literature-review-agent.dk` | The public address used in the sitemap, robots.txt, canonical links and link previews, so search engines see one address whatever host name a visitor used. `appsettings.Development.json` sets it to empty, which uses the address of the request (`http://localhost:5038` with `dotnet watch`). Set your own address here when you deploy a copy elsewhere |
| `Runs:ScreeningReviewTimeoutHours` | 24 | How long a run waits for the human screening review |
| `Llm:Provider` | Mistral | `Mistral` or `OpenAICompatible` |
| `Llm:Model` | mistral-large-latest | The model name sent to the provider |
| `Llm:BaseUrl` / `Llm:ApiKey` | empty | Address and optional key of an OpenAI-compatible server |
| `Llm:FastModel` | mistral-small-latest | A second model for the narrow, high-volume checks of a systematic review: the peer-review filter, both screenings, and the citation check and re-check (`LlmOptions.FastStages`). Everything that writes or reasons stays on `Llm:Model`. The methods text, the protocol, the AI-use statement and `llm-calls.json` name the model of each step, and screening decisions are cached per model. Empty (the code default) uses `Llm:Model` for everything. The multivocal review routes the same stages the same way (`LlmFactory.CreateForReview` in `Program.cs`): its two screenings are labelled `screening` and `screening-second` by `MultivocalScreener` and its citation checks `citation-check` and `citation-recheck` by the shared grounding, so they go to the fast model, while the quality checklist, the map, the extraction, the synthesis, the writing, the repair and its sense check stay on `Llm:Model`. Its screening and citation-check caches are keyed on the model that answers them, `multivocal-screening.json` and `mlr-paper.json` (`CheckModel`) name that model, and its statement on the use of AI names both models and what each did |
| `Llm:FastReasoningEffort` | none | The `reasoning_effort` sent to `Llm:FastModel`; `none` switches reasoning off on Mistral Small 4. A model that rejects it is asked again without it |
| `Llm:ReasoningEffort` | low | Mistral only: the `reasoning_effort` sent with every call (`low`, `medium` or `high`). Mistral Large 4, which `mistral-large-latest` can point to, always reasons and uses `high` when the setting is left out, which makes a review several times slower. Empty leaves the setting out. A model that rejects it is asked again without it, and the run stops sending it |
| `Llm:ScreeningParallelism` | 8 | How many model calls one run makes at the same time (screening, extraction, coding, checks). With a reasoning model each call takes 30 to 110 seconds, so more calls at once shortens a review; with `Runs:MaxConcurrentRuns` at 3 the server can have up to 24 calls in flight, and a 429 from the model is retried with backoff. The two screenings of a record run at the same time |
| `OpenSources:ContactEmail` | empty | Contact address for OpenAlex, Crossref and Unpaywall |
| `OpenSources:SemanticScholarApiKey` | empty | Optional Semantic Scholar key (secret) |
| `OpenSources:OpenAlexApiKey` | empty | OpenAlex key (secret); without it most OpenAlex queries are refused once the small keyless budget is used up |
| `OpenSources:ChainingPerPaper` | 5 | References and citing papers taken per included paper in citation chaining |
| `Cache:Enabled` | true | Reuse search responses and screening decisions; every reuse is marked in the ledger |
| `Cache:SearchResponseHours` | 24 | How long a search response is reused |
| `Cache:ScreeningDecisionDays` | 90 | How long a screening decision is reused |
| `Synthesis:ThematicSynthesis` | true | Code the studies and write one subsection per theme |
| `Synthesis:DualCoding` | true | Second, independent theme assignment with Cohen's kappa |
| `Synthesis:RepairCitations` | true | Rewrite partly and not supported citations, then check again; a second pass rewrites what is still partly supported and removes what is still not supported |
| `Synthesis:SecondCitationCheck` | true | Independent second check of partly and not supported citations |
| `Synthesis:MaxThemes` | 8 | Upper limit on themes |
| `Synthesis:CodingBatchSize` | 6 | Studies per coding call |
| `Metrics:Enabled` / `Metrics:Folder` | true / `App_Data/metrics` | The run metrics store behind the Metrics page |

## Several users at once

Each run gets its own id, folder under `WorkspaceStore/` and link, so users never see or overwrite each other's runs. At most `Runs:MaxConcurrentRuns` runs call the model at the same time; further runs wait in line and their dashboards show the position. Rate-limit and temporary server errors are retried with a growing delay. A public deployment runs on the server's own Mistral key, so each visitor gets a small daily quota; visitors with their own key only have a light hourly cap. Client addresses are never stored, only a keyed hash that is enough to count runs.

A run lives in the app's memory while it works, so it cannot survive an app restart; at start-up, runs that were cut off are marked "Interrupted". On Simply (IIS), set the application pool's idle time-out to 0, move the scheduled recycle to a quiet hour, and switch on WebSockets.

## What the checks do and do not catch

The safeguards reduce the problem of unreliable citations but do not remove it. The range check only guarantees that every `[n]` exists in the reference list. Whether the paper says what the sentence claims is judged by the citation support check, which is itself a model judgement on excerpts; for papers without an open-access full text it only sees the abstract, which is why such gaps are shown as "not enough text to check" rather than as errors. The second check and the repair pass lower the number of partly and not supported citations, and both only accept evidence that is verbatim in the paper, but what remains orange or red stays visible in the report on purpose. References are built from source metadata, so they are only as good as what the database returned. The two screenings and the two codings are two prompts to the same model, so their agreement is an upper bound on what two human reviewers would reach. The extraction and MMAT appraisal are a model's reading of the text. The prompt-injection markers, the phrase scans and the input checks on the configuration panel make steering attempts visible; they do not make them impossible.

The ledger is what makes errors findable: before using any output, follow a sample of claims from the report back through the citation audit and the ledger to the source PDFs.
