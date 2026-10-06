# 7. Extending and testing

This page is for the person about to change something. It lists the common changes, the places each one touches, and the traps that break reproducibility or the audit trail. It ends with how the tests are organised and how to run them.

## Common changes

### Adding a bibliographic source

A source is a class that implements `IAcademicSource`: a `SourceName`, `FetchPapersAsync(query, maxResults)` that returns `AcademicPaper` records, and `LastRawResponses` with the raw bodies of the last call. Do not skip the raw responses; they are what lets someone show later what the search returned on the day.

1. Write `XyzSource.cs` next to the others. Use `OpenSourceHttp.GetStringAsync` for an open API, so retries and the `User-Agent` are handled for you. Fill `Doi` and `PdfUrl` when the API offers them: the DOI drives de-duplication and citation chaining, and the PDF link is tried for full text.
2. Add an entry to `SourceCatalog` with a key, a display name and the kind of API key it needs.
3. Add the key to the `switch` in `RunReviewAsync` that creates the sources, and, if it needs a key, to `GetSourceAvailability`.
4. Add a parser test with a saved real response, like the ones in `OpenSourceAndSafetyTests.cs`.

### Changing a prompt

Screening decisions are cached for 90 days under a key that includes `ScreeningPromptVersion` (`PrismaReviewEngine.Screening.cs`). **Whenever you change either screening prompt, bump that constant**, or runs will keep reusing decisions made with the old prompt. The search cache key has its own version (`"search-v1"` in `FetchSourceAsync`); bump it if you change how search responses are turned into records. Other prompts (extraction, outline, report, citation check) are not cached, but the prompt hash in `llm-calls.json` still changes, so runs made before and after the change can be told apart.

The screening prompt lives only in `ScreenPaperAsync`, which the evaluation tool also calls. Keep it that way, so the tool keeps measuring what the app does.

### Adding a model step

Wrap the call in `using (LlmStage.Begin("your-stage"))`, so it is recorded under its own name in `llm-calls.json`. If the stage takes noticeable time, call `ReportProgress` with the step it belongs to, so the dashboard's progress bar keeps moving. If its prompt contains the reviewer's query, objective or criteria, include `PromptSafety.ReviewerInputNotice`. If the answer is structured, use `LlmJson.GetAsync<T>` with a validation function and `JsonMode(temperature)`: it asks for JSON, checks the answer, and gives the model one chance to fix a bad answer. Put any text from papers inside `PromptSafety.Wrap(...)` and add `PromptSafety.DataOnlyNotice` to the prompt. If the model is asked to back something with a quote, check the quote with `CitationSupportChecker.QuoteOccursIn` rather than trusting it. Use temperature 0 for anything that decides or extracts. Save what the step produced to the run folder, and add the file to `rootFiles` in `BuildWorkspaceArchive` so it lands in the archive and the manifest.

### Adding a kind of review

Each kind of review is a module in `Modules/<Name>/` that implements `IReviewModule` (`Pipeline/ReviewModule.cs`) and is listed in `ReviewModules.All`. The module declares its cards for the start page; give a new card a place in `StartPageOrder` in `Pipeline/ReviewMethod.cs`, and keep `Available: false` until the module can run it. A variant of an existing review, such as the rapid review, is a new card on that module rather than a new module. Files that only one module uses go in its folder; anything two modules share goes in a stage folder. The order of the work is in [docs/roadmap/review-modules.md](../roadmap/review-modules.md).

### Adding a field to the ledger or the report

Add a property to `ReviewState`, `ReviewStats`, `ScreeningLog` or `PrismaReport`. Do not rename or remove existing ones: the ledger is read back from JSON, and older runs would lose that data. If the field is a count that should appear in the PRISMA flow, update `PrismaFlowCounts` and make sure `IsConsistent` still holds.

### Changing the landing page or other components

The CSS is compiled ahead of time. After changing a `.razor` file, run `npm ci` once and then `npm run build:css` in `AuBtechReviewAgent`, and commit `wwwroot/css/tailwind.css` along with the component. Tailwind picks up class names from ordinary text too, so even a wording change can change the CSS; the CI check tells you when you forgot. Never build a class name from a variable (`"bg-" + colour`), because Tailwind cannot see it.

### Adding a term to the glossary

Add an entry to `Glossary.All` in `Glossary.cs` (a key, the term, and one or two plain sentences), then use it in a component with `<Term Key="your-key" />`, `<Term Key="your-key">other words</Term>`, or `<Term Key="your-key" Icon="true" />` for a small "?" after a label. Use `Below="true"` near the top of a scrolling panel. It appears on `/glossary` by itself, and a test fails if a component uses a key that does not exist.

### Checking accessibility locally

Start the app (`dotnet watch` in `AuBtechReviewAgent`), then in `tools/a11y` run `npm ci`, `npx playwright install chromium` and `node axe-check.mjs`. `BASE_URL` changes the address (default `http://localhost:5038`) and `A11Y_PAGES` the pages (default `/,/review,/metrics,/glossary`). Also try every changed page with the keyboard only: Tab through it, check that focus is always visible, and that Escape closes what it opened.

## Tests

All tests are in `AuBtechReviewAgent.Tests` and run offline: no API keys, no network. Run them from the repository root with `dotnet test`; CI runs the same command. The tests sit in the same folders as the code they test (`Screening/`, `Verification/` and so on), with whole runs in `Pipeline/` and the fakes in `Support/`; the table below lists them by file name.

The engine has test hooks so a whole review can run against fakes. `ChatFactory` replaces the language model (usually with `FakeChatService`, which answers by matching text in the prompt), `SourceFactory` replaces the sources (`PipelineTests.FakeSource`), `CitationGraphFactory` replaces OpenAlex for chaining, `FullTextFetcher` replaces PDF downloads, `Cache` replaces the cache (`ReviewCache.Disabled` by default in tests), and `WorkspaceRoot` points the run folders at a temporary directory, which `TestFolders.TryDelete` removes afterwards.

The **module test kit** (`Modules/ModuleKit.cs` and `Modules/ModuleKitTests.cs`) checks the rules of `AGENTS.md` for every kind of review. `ModuleKit` runs each module once end to end with a fake model and a poisoned fake source: the source carries an instruction aimed at the model and a `javascript:` link, and the server key is a distinctive value. The tests then check that every accepted citation rests on a verified quote, that the code-written parts of the report equal what the code writes from the ledger, that `run.json` names the module and every file of the run folder is in the archive, that the injected text reached the model only inside the untrusted-text markers, that no key is in the archive, and that the report links no address that is not http(s). Every module in `ReviewModules.All` must have an entry in `ModuleKit.Runners`; a module with an available card must be runnable, and its key goes into the `InlineData` of each rule. Rule 4 (prompt versions) is left to the module's own tests, since only the module knows which prompts decide.

| Test file | What it covers |
|---|---|
| `ModuleKit.cs`, `ModuleKitTests.cs` | The rules of `AGENTS.md`, checked for every kind of review on a poisoned fake run |
| `PipelineTests.cs` | Whole runs end to end: archive contents, manifest verification, protocol, human review pause, interrupted runs |
| `PipelineFeatureTests.cs` | Dual screening and disagreements, injection flags, citation chaining, cache reuse, deleting a run, parallel screening order |
| `RealRunRegressionTests.cs` | Cases taken from real runs that once went wrong |
| `OpenSourceAndSafetyTests.cs` | Source parsers on saved real responses, rate-limit handling, PDF size limit, prompt-injection markers |
| `ArtifactAndProseTests.cs` | Markdown turned into prose, artifact validation and drawing (Mermaid, TikZ), custom requests, compacting after repair, progress that never moves back, a full run with an artifact and with none |
| `InputGuardAndProgressTests.cs` | Input normalisation, length errors, instruction flags and the protocol entry, API key cleaning, the progress plan and percentage, monotonic progress through a full run |
| `ArxivSourceTests.cs` | arXiv: retry on 503, a lasting 503 still fails the source, requests one at a time and spaced out |
| `VerificationAndMetricsTests.cs` | Attributed clauses, the rubric and insufficient evidence, the second check and its quote rule, repair targets, token parsing, the metrics store, metrics of a full run |
| `ThematicSynthesisTests.cs` | Coding anchors, the codebook guard, second-coding kappa, the revision guard, citation repair, a full run with themes and the fallback when coding fails |
| `CitationSupportCheckerTests.cs`, `CitationValidatorTests.cs` | Quote matching, sentence splitting, range stripping |
| `MethodsSectionWriterTests.cs`, `MmatTableTests.cs` | Generated methods text; the MMAT table and its note |
| `ReportGuardTests.cs`, `GroundingAndFlowTests.cs` | `main.tex` details, grounding context, PRISMA flow counts, run clean-up |
| `ApaCitationBuilderTests.cs`, `JournalRankingMatcherTests.cs`, `MermaidSanitizerTests.cs` | Reference formatting, quartile lookup, diagram cleaning |
| `RunCoordinatorTests.cs`, `RunQuotaServiceTests.cs`, `ScreeningReviewTests.cs` | Slots and queue, quotas and tiers, applying a human review |
| `ExportAndEvaluationTests.cs`, `RemovedRecordsTests.cs`, `SecurityUtilityTests.cs` | BibTeX/RIS, kappa and recall maths, removal reasons, input cleaning |

When you fix a bug found in a real run, add a case to `RealRunRegressionTests.cs` with the input that caused it.

## Measuring screening accuracy

`tools/ScreeningEval` runs the app's own screening prompt over a dataset where human reviewers made the inclusion decisions (any ASReview-format CSV, for example from the SYNERGY collection) and reports recall, precision, specificity, F1 and Cohen's kappa, plus every paper the model missed. The usage is in the comment at the top of `tools/ScreeningEval/Program.cs`. It uses the first screening prompt only, needs a real API key and costs one model call per paper, so run it deliberately rather than in CI.
