# 11. Compliance

TraceableAI writes literature reviews with a language model, so what it hands over is AI-generated text. This page describes how the app says so, to a person reading a report and to a machine reading a file, and where the rest of the site's legal duties are described. The site is run by the Department of Business Development and Technology at Aarhus University and is meant to behave like an official Aarhus University site: it has a privacy notice, an accessibility statement and copyright notes, as any AU site does. Those are kept on their own pages, and the sections at the end of this page point to them.

## Article 50 of the EU AI Act

Article 50 of the EU AI Act sets transparency duties for AI systems that interact with people or generate content. Article 50(2) asks the provider of a system that generates text to mark its output in a machine-readable format, so that it can be detected as artificially generated. The duty applies to systems already on the market from 2 December 2026. Article 50(4) adds a visible disclosure for AI-generated text published to inform the public. TraceableAI meets both in code: no mark is written by the model, so no run can leave one out or word it differently.

### What a reader sees

A reader is told in three places that a report was written by AI. The paper's front page names the tool as its author, "TraceableAI – artificial researcher", with a footnote saying that it is an AI system and that the person who planned the review is responsible for its use, and a second note saying that the review has not been peer reviewed (`ArticleInfo.Author`, `AuthorNote` and `NotPeerReviewed`, drawn by `Components/Paper/ArticleFront.razor`). The line above the title in main.tex reads "AI-GENERATED SYSTEMATIC LITERATURE REVIEW", or the multivocal review's own label. The declarations carry a statement on the use of artificial intelligence, written in code from what the run recorded: which model did which steps, what a person checked and how the citations held up (`Report/AiUseStatement.cs` for the systematic review, `MultivocalPaper.AiUse` for the multivocal review).

### What a machine reads

Every output of both kinds of review also carries machine-readable marks, all written by `Report/AiMarking.cs`. The table lists them.

| Output | Where the mark is | What it says |
|---|---|---|
| Report pages (`/spec-matrix/{id}`, `/gallery/{id}`, `/mlr-report/{id}`, `/mlr-paper/{id}`) and their printed copies | Meta tags in the page's head, written by `Components/Layout/AiGeneratedMeta.razor` through `SeoHead`'s `AiGeneratedBy` | `ai-generated` is `true`; `generator` names TraceableAI with its version and commit, the department and the models; `keywords` is `AI-generated`; `ai-models` lists the models when any is recorded |
| The same pages | A schema.org `Report` in a `application/ld+json` block | The creator is the TraceableAI software, `digitalSourceType` is `TrainedAlgorithmicMediaDigitalSource` and `keywords` is `AI-generated` |
| main.tex of both kinds of review | A comment at the top of the file | That the document is AI-generated, by which tool and models, and that the prose was checked but not peer reviewed |
| A PDF made from main.tex | The PDF's document info, set by `hyperref` with `\hypersetup` | Title, author (TraceableAI, an AI system), subject and keywords that say "AI-generated", creator and producer naming TraceableAI |
| The run archive | `manifest.json` | `"AiGenerated": true` and a `Generator` block with the tool's name, version, department, address, models and the same statement |

The JSON-LD block does not break the Content-Security-Policy. The policy allows only scripts from the site itself, but a script block of type `application/ld+json` is data: the browser never runs it, and the landing page already uses one for its own schema.org description. The meta tags are there as well, so a reader that ignores JSON-LD still finds the mark.

In main.tex, `hyperref` is loaded with `hidelinks`, so links are not boxed or coloured and the printed page looks as it did before. It is loaded last, as `hyperref` asks. The PDF's document info is the place most readers of a PDF look first; a full XMP packet would need the `hyperxmp` package, which not every TeX installation has, so it is left out for now. The manifest's keys are in the same style as its other keys, and the manifest's own fingerprint covers them, so the mark cannot be removed from a downloaded archive unnoticed.

The marks change the systematic review's main.tex on purpose: the comment and the `hyperref` lines are new, and the golden file `AuBtechReviewAgent.Tests/Report/Golden/systematic-main.tex` was written again for them. The rest of the document is unchanged. `AiMarkingTests` checks that every mark is present on a report page, in main.tex of both kinds of review and in the manifest.

## Personal data

The privacy page (`/privacy`, `Components/Pages/Privacy.razor`, with its words in `Privacy.razor.cs` and `Web/PrivacyText.cs`) is the site's privacy notice. It names the Department of Business Development and Technology (BTECH), Aarhus University, as the data controller, and Mistral AI and the hosting provider as processors. It says what each part of the code keeps and for how long: network addresses are used in memory only, for the rate limits, the form bot guard and the run queue, and are written to disk only as a keyed fingerprint for the daily run quota; a run's question, settings and results stay in its run folder until it is deleted after `Runs:RetentionDays`; edit keys live in the visitor's browser; a report reaches the gallery only after its owner says yes, with the time of that yes recorded, and can be withdrawn; and the Metrics page holds quality figures without questions or addresses. The periods on the page come from the running configuration, so the page cannot drift from the server's settings. It also lists the rights under the GDPR and the complaint to Datatilsynet. Page 6 describes the page among the others.

## Accessibility

The accessibility statement (`/accessibility`, `Components/Pages/Accessibility.razor`) follows the order of the statements that Danish public bodies write in Digitaliseringsstyrelsen's WAS-Tool: which site it covers, the compliance status (partially compliant with WCAG 2.1 level AA, the level EN 301 549 sets), the content known not to be accessible with its success criterion, how the statement was prepared and when, where to send feedback, and the enforcement body, Digitaliseringsstyrelsen. The known gaps are real ones found in the code, such as the report's diagram, which has no text description. The statement is checked by axe-core in CI like the other trust pages. If TraceableAI becomes an Aarhus University service, the statement moves to was.digst.dk and the page links to it.
