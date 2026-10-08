# Review modules: one module per kind of review

This note analyses how the code is built today and proposes how to turn each kind of review into a module of its own, before the multivocal review (MLR) is built. It is a design to agree on; nothing here is implemented yet. The detailed MLR plan is in [mlr-todo.md](mlr-todo.md); this note replaces its block A and the folder layout in its block B.

## 1. What the code looks like today

The folders were reorganised by pipeline stage in #24 and look modular, but most of the work is still done by one class. `PrismaReviewEngine` is 3,859 lines spread over nine partial files in seven folders (`Pipeline/`, `Search/`, `Screening/`, `Report/`, `Synthesis/`, `Verification/`, `Runs/`). All of them share one `RunContext` and one ledger, `ReviewState`, whose fields are those of a PRISMA review. `RunReviewAsync` fixes the order of the stages in code: protocol, search and screening, citation chaining, the human screening check, full texts, synthesis.

The table below sorts the code by how tied it is to the systematic review. The left column is already a shared core; the middle column is shared in spirit but lives inside the engine; the right column is PRISMA only.

| Already independent | Generic, but locked inside the engine | Belongs to the systematic review only |
|---|---|---|
| `Sources/` (`IAcademicSource`, `SourceCatalog`) | Run folder, loading and saving the ledger (`Pipeline/PrismaReviewEngine.cs`) | The order of stages in `RunReviewAsync` and the stage names |
| `Llm/` (`LlmJson`, `PromptSafety`, the call recorder) | Edit keys, reviewer notes, deleting a run (`Runs/PrismaReviewEngine.*.cs`) | `ReviewState` and `ReviewStats` as they are |
| `Verification/CitationSupportChecker` (a static service) | Re-checking a citation (`Verification/PrismaReviewEngine.Recheck.cs`) | `PrismaFlowDiagram`, `MethodsSectionWriter` (PRISMA and MMAT wording) |
| `Evidence/DocumentRAGUtility`, `StudyExtractor` | Screening one record with a prompt (`ScreenPaperAsync` is already static) | The LaTeX report and the PRISMA checklist in `Report/` |
| `RunCoordinator`, `ReviewCache`, `RunMetricsStore` | The citation repair loop (`Synthesis/PrismaReviewEngine.Thematic.cs`; moved to `Verification/` in M3) | `PrismaFunnel`, `ScreeningReviewPanel` |
| `RunManifest`, `ApaCitationBuilder`, `BibliographyExporter` | Building the archive: zip, manifest, hashes (`Report/PrismaReviewEngine.Archive.cs`; moved to `Report/RunArchive.cs` in M4) | `Home.razor` (1,579 lines) and `SpecMatrix.razor` (1,258 lines) |

Two things follow from this. First, every page that only wants to open a run, check an edit key or save a note has to inject the whole `PrismaReviewEngine`, so a second engine would have to copy those parts or depend on the first. Second, `ReviewMethods.All` is a list of cards with no link to code: a card says what a method is, but nothing says which code runs it.

## 2. The idea: core, steps, modules and cards

The proposal has four layers. Each layer only uses the layer below it, so a person or a coding assistant can find any piece by asking which layer it belongs to.

```
  Start page        Systematic   Rapid   Living        Multivocal   Grey literature      ← cards (what the user picks)
                         \         |       /                 \          /
  Modules            Systematic module                  Multivocal module                ← recipes (the order of steps)
                         |   \_____________ shared steps ______/   |
  Steps              protocol · search · screen · full text · extract · synthesise · check  ← reusable building blocks
                         |                                         |
  Core               run folder · ledger header · edit keys · notes · model access · sources · grounding · archive
```

| Layer | What it is | Example | Where it lives |
|---|---|---|---|
| Core | Services every run needs, whatever the method | Opening a run folder, checking an edit key, checking a quote against a source | The stage folders as today (`Runs/`, `Llm/`, `Sources/`, `Verification/`, `Report/`) |
| Steps | One stage of a review, written once and used by several modules | Screening records twice with two prompts; checking every citation of a text | The stage folders as today (`Search/`, `Screening/`, `Evidence/`, `Synthesis/`) |
| Module | One kind of review: its recipe of steps, its own ledger, its pages and its report | The multivocal module runs search twice (academic and grey), adds the quality checklist and the systematic map, and writes a report following Garousi | `Modules/<Name>/`, one folder per module |
| Card | What the user picks on the start page: a module with preset options and its own reporting standard | "Rapid Review" is the systematic module with single screening and no citation chaining, and states every shortcut | Declared by the module, so a card cannot exist without code behind it |

The important choice is that **five cards need only two modules**. A rapid review and a living review are the systematic module run with different settings, and a grey literature review is the multivocal module with the academic pool switched off. This keeps the number of engines small and means a fix to the systematic module also fixes its variants.

| Card | Module | What the card changes |
|---|---|---|
| Systematic Literature Review | Systematic | Nothing; the default |
| Rapid Review | Systematic | Shortcuts chosen from the Cochrane guidance, for example single screening or no citation chaining; the report lists each one |
| Living Systematic Review | Systematic | Search reruns on a schedule; each update round is recorded and dated, and the report says what changed since the last round |
| Multivocal Literature Review | Multivocal | Nothing; the default |
| Grey Literature Review | Multivocal | The academic pool is off |

## 3. The module contract

Every module implements one small interface. It is registered once in `Program.cs`, and the start page, the run list, clean-up and the shared pages read everything they need from it. A sketch, not final code:

```csharp
public interface IReviewModule
{
    string Key { get; }                              // "systematic", "multivocal"; written to every run it starts
    IReadOnlyList<ReviewMethod> Cards { get; }       // the cards it offers on the start page
    string RoutePrefix { get; }                      // "/review", "/mlr"
    IReadOnlyList<RunStage> StagesFor(ReviewMethod card); // drives the progress bar and the run view
    Task RunAsync(RunHandle run, CancellationToken cancel);   // the recipe
    IReadOnlyList<string> ArchiveFiles(RunHandle run);        // what goes in the archive besides the core files
}
```

M1 builds the first part of this contract: `Key`, `Name`, `RoutePrefix` and `Cards`, in `Pipeline/ReviewModule.cs`, with the two modules in `Modules/`. `ArchiveFiles` followed in M4 (as a list of file names, since the files a module keeps do not depend on the run). `StagesFor` and `RunAsync` come with the first multivocal stage (MLR block C): they need a request that both modules understand, and its shape follows from the MLR's planning form, so designing it earlier would fit it to the systematic review alone. The multivocal module joins the test kit at the same point; the kit already refuses any card that is switched on before its module can run.

Each run folder gets a small header file, `run.json`, written by the core before anything else: the run ID, the module key, the card key, when it started, its stage and when it finished. The core reads only this header to list, open, clean up or redirect a run, so it never needs to understand a module's ledger. Opening `/review/{id}` for an MLR run sends the user to `/mlr/{id}`. Runs made before this change have no header and are treated as systematic runs, the same way runs without `owner.json` stay editable today.

Each module keeps its own ledger next to the header. The systematic module keeps `transparent-process.json` with exactly its current shape, so old runs, the demo run and downloaded archives still open and their hashes still match. The multivocal module gets its own ledger file with the formal and grey pools kept apart.

## 4. Building a module while production stays unchanged

A module can be switched on per environment. A new setting, `ReviewModules:Preview`, lists modules that are visible even though their cards say "coming soon". `appsettings.Development.json` would list `multivocal`, and production would list nothing. In production the MLR card stays "coming soon" and the `/mlr` pages answer "not found", so the code can be merged to master, tested in CI and deployed without anyone seeing it. In Development the card shows a "Preview" chip and opens the module. Switching on for everyone is then the existing `Available` flag on the card. This setting is not a key, so it is safe in the settings file, but it is a change to your settings file and I will show it to you before writing it.

## 5. Rules every module must keep, as tests

The seven rules in `AGENTS.md` are what makes TraceableAI trustworthy, so they should not depend on each module remembering them. A shared test kit runs every registered module through a complete fake run (the scripted fake model that the tests already use) and checks the rules automatically:

| Rule | Test run for every module |
|---|---|
| No model text unchecked | Every value claimed from a source has a quote found in that source's text |
| The method is described by code | The methods text and counts are equal to what the ledger says, with no model call involved |
| Every decision on file | Every record found has a decision and a reason in the ledger; `run.json` names the module |
| Prompts versioned | A changed screening prompt cannot reuse a cached decision |
| Text from sources is data | An injected instruction in a source is wrapped and ignored |
| No secrets | The archive contains no key from the settings |
| Only web links | Every link in the report is http(s) |

A new module is not done until it passes the kit. This is the "intelligent" part of the design: the core promises are enforced once, centrally, and every future review type inherits them.

## 6. Folder layout after the change

The stage folders stay where they are and become the core and the shared steps, so #24's layout is kept and nothing needs to move at once. Each module gets one folder with everything that belongs only to it, including its pages, so a reader finds a whole review type in one place.

```
AuBtechReviewAgent/
  Sources/ Llm/ Runs/ Verification/ Report/        core
  Search/ Screening/ Evidence/ Synthesis/           shared steps
  Pipeline/                                         IReviewModule, the module registry, run.json, coordinator, progress
  Modules/
    Systematic/                                     SystematicModule.cs, its ledger, PRISMA report, flow diagram, its pages
    Multivocal/                                     MultivocalModule.cs, its ledger, grey search, quality checklist, map, its pages
  Components/                                       pages shared by all modules: landing, start, about, verify, layout
```

The systematic files move into `Modules/Systematic/` gradually, one or two per pull request, when they are touched anyway. `Home.razor` and `SpecMatrix.razor` move last, and are split into a planning form, a run view and a report page on the way. Razor pages in `Modules/` get the folder as their namespace by default; an `_Imports.razor` in each module folder keeps the single `AuBtechReviewAgent` namespace rule.

## 7. Order of work

Each step is one small pull request, changes nothing a user can see, and keeps all existing tests passing. That is the proof that the systematic review still works exactly as before.

| Step | Pull request | Main files |
|---|---|---|
| M1 (done) | The contract and the registry: `IReviewModule`, `run.json`, a `SystematicModule` that calls the existing engine; the start page reads its cards from the registry | `Pipeline/` |
| M2 (done) | Run services out of the engine: the run folder, ledger loading, edit keys, notes and deletion become a `RunStore` that pages use instead of the engine | `Runs/`, the pages that inject the engine |
| M3 (done) | Grounding as a service: the citation check, the repair loop and the re-check work on any text and any set of sources (`Verification/Grounding.cs`). A web page's text uses the same `ReferencedPaper` record as a paper; the prompts name the kind of review, and their word "paper" is adapted for grey sources in MLR block D | `Verification/`, `Synthesis/` |
| M4 (done) | The archive split: the core builds the zip, manifest and hashes; each module adds its own files and report | `Report/` |
| M5 (done) | The module test kit, run for the systematic module | `AuBtechReviewAgent.Tests/Modules/` |
| M6 (done) | The preview setting (`ReviewModules:Preview`, Development only), the multivocal preview page at `/mlr`, and runs that open on their own module's pages. The run contract and the kit entry follow with MLR block C (see section 3) | `Modules/Multivocal/`, settings |
| M7 (done) | The evidence a review writes from, as one shape for every module: numbered sources with their text, quote-verified findings, themes with their sources and codes. The systematic review fills it from its extraction and codebook (`Synthesis/ReviewEvidence.cs`, `Modules/Systematic/SystematicEvidence.cs`), and a test shows its writers are shown the same text as before | `Synthesis/`, `Modules/Systematic/` |
| M8 | The writing stages as a shared step: grounded outline, theme sections and fill pass, discussion, automated peer review and revision, introduction and abstract, style pass. A module says how to name the review and which sections it adds; the systematic review's prompts and files stay the same | `Synthesis/`, `Writing/` |
| M9 | The paper as one shared shape, one paper component for the output pages, and one `main.tex` builder into which a module puts its method section, figures and appendices | `Report/`, `Components/` |

After M6 the MLR blocks C to F in [mlr-todo.md](mlr-todo.md) are built inside the multivocal module. Block G then lifts the writing and checking stages out of the systematic review (M7 to M9), so every kind of review goes through one overall process and plugs its own sub-steps into it; the MLR is the second module to use it. The rapid and living cards become settings on the systematic module.

## 8. What we deliberately do not do

There is no plugin loading from separate DLLs and no general workflow language: modules are ordinary classes compiled with the app, and a recipe is ordinary C# code, which is easier to read and test. The systematic ledger keeps its file name and shape. Modules do not call each other; anything two modules share moves down into a step or the core. Large page files are not split in the same pull request that moves them.
