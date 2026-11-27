# Changelog

All notable changes to TraceableAI are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version starts with 0, the method and the output may still change between minor versions.

Each version's section becomes the notes of its GitHub release, so write it like a game's patch notes, for someone deciding which version to use or cite. Versions after 0.6.0, and [Unreleased], follow the shape below; the release check stops the release when they break it. Versions up to 0.6.0 stay in the older Keep a Changelog form (**Added**, **Changed** and **Fixed**, with exactly one emoji) and are never rewritten. Changes that are merged but not yet released go under [Unreleased]. How to publish a version is in [CONTRIBUTING.md](CONTRIBUTING.md#publishing-a-version).

A section starts with a signed dev note: a blockquote that begins `> **Dev note:**`, two to four casual sentences on what the patch is about and why, and the signature `> — The TraceableAI team`. Then comes `### 🌟 Patch highlights`, one short paragraph with the headline changes for someone running a review and whether a new run can differ from the last version. After it come the areas that have changes, one `###` section each, in the order of the table. Inside an area, a `####` heading names a stage or a feature, such as Screening or Themes. Under it a short "why" quote (`>`) says why it changed, and one bullet follows for each change. A change with a before and after is written as `- **Label**: old ⇒ new`, with one arrow and exact values; a change without a clean before and after is a plain bullet. The last section, `### 🐛 Bug fixes`, is a list in which every item starts with "Fixed a bug where".

| Area | Emoji |
|---|---|
| Patch highlights | 🌟 |
| Systematic review | 🔬 |
| Multivocal review (preview) | 🧭 |
| Report page | 📄 |
| Self-check | ✅ |
| Search and sources | 🔍 |
| Site | 🌐 |
| Bug fixes | 🐛 |

Each `###` heading has exactly one emoji, at its start, and no emoji appears anywhere else. The notes must score at least 60 for Flesch Reading Ease.

## [Unreleased]

## [0.7.0] - 2026-11-27

> **Dev note:** This patch is about telling the truth in the paper. We read your criteria more kindly, taught the themes to build on each other and told the abstract to stop shouting "we". The report page lost a few annoying habits on the way.
> — The TraceableAI team

### 🌟 Patch highlights

Screening now treats "or" in your inclusion criterion as a choice, so fewer good records are dropped. The themes build on each other, the abstract and conclusion claim no more than the evidence allows, and the concept map shows at most three dashed links. Diagrams the model writes are checked before they reach main.tex, and the live site is now updated only when a version is published. A new run can differ from 0.6.0: the screening and language edit prompts have new versions, so old cached screening decisions are not used again.

### 🔬 Systematic review

#### Screening

> A criterion such as "agent framework or orchestration loop" was read as needing both. A good study was dropped in code and stayed only because the second screening disagreed.

- **Screening prompt**: screening-v7 ⇒ screening-v8
- **A criterion with "or"**: every option is needed ⇒ one option is enough
- The record needs to show one of the options, not all of them, so fewer good records are dropped in code.
- Old cached screening decisions are not used again, because the prompt has a new version.

#### Themes

> The themes said the same things again and again. A study that only touched a theme was stretched to fit, and the rationale read like a list.

- **Earlier findings in a later theme**: restated with their figures ⇒ pointed back to
- **A big theme**: lists each result ⇒ compares how its studies test the question
- **A study that only touches a theme**: stretched to fit ⇒ called indirect, or left out with its reason on file
- **The rationale**: one study per sentence ⇒ what is known, what is missing and why it matters

#### Abstract and conclusion

> The abstract said "We removed 0 duplicates", although the author is software, and it claimed more than its moderate-certainty answers allow.

- **Language edit prompt**: language-edit-v3 ⇒ language-edit-v4
- **A count of zero**: "removed 0 duplicates" ⇒ "no duplicates were found"
- **Wording at moderate or low certainty**: "show that" ⇒ "suggest" or "may"
- The abstract no longer says "we", because the author is software.
- The conclusion claims no more than the certainty table allows, as the abstract does.
- A sentence no longer names a study and cites it in the same breath.

#### Concept map

> In a real run, 12 of 15 links were dashed, so the map told the reader little about the literature.

- **Dashed links**: no limit ⇒ at most 3
- The dashed links that are the review's own reading and come after the first three are left out of the map and listed with a reason.
- The methods say how many links were left out.

#### Methods and references

> The methods and the checklist said things that were not quite true.

- **The citation check**: told in the discussion ⇒ told in the methods, as one story that adds up: what the first check found, what the second check changed, what the repair rewrote or dropped, and how many citations are left
- **A study whose DOI is not at Crossref**: counted as a failed check ⇒ counted as not checked for retractions
- **Book chapters**: counted as journals ⇒ given their own count
- Table A.1 in main.tex starts with the reference number, so you can match a row to its [n].
- The PRISMA checklist now tells the truth about a pilot run's title.
- The methods count reviews by their study type.
- The cap on records per source and the research questions are each said once, and some small grammar slips are gone.

#### Figures in main.tex

> A diagram the model wrote in TikZ went into main.tex as it was, and the paper itself was never compiled in CI.

- A diagram the model writes is checked in code and drawn again with fixed styles. One that tries to read or write files or define commands is left out, and the choice is saved in diagram-check.json.
- The flow diagram and the pie chart escape their labels the same way as the rest of the paper.
- CI now compiles the systematic and the multivocal main.tex, so a paper that does not compile stops the build.

### 📄 Report page

#### Paged report

> The paged report broke in small ways, most of all in a narrow window.

- **Minimum table width in a paged sheet**: 700 px ⇒ none
- A citation's card comes into view when you open it, also from "Show the first".
- A heading no longer sits alone at the foot of a page.
- A table's caption and header row move to the next page with its first row.
- The appendix table fits a narrow page without breaking words letter by letter.

### ✅ Self-check

> The self-check gave a wrong outcome on two of its checks, and it did not read the concept map.

- The self-check knows the count of records that could not be screened.
- It flags a figure given in two themes.
- It counts a figure that the concept map repeats.
- It notes when more than half of the map's links were dashed.

### 🔍 Search and sources

> The search never filled its cap for a source, and the keywords read like search strings.

- **Keyword phrase from a search string**: up to 5 words ⇒ at most 3 words
- A source now gets more records when some drop out for their year or as duplicates, so it can fill its cap.
- Keywords are short terms now, not whole search strings.

### 🌐 Site

#### Run page

> The tab said "2% · Your review" for the whole run.

- **Tab title while a review runs**: "2% · Your review" for the whole run ⇒ the run's progress
- The tab says when the review is ready.
- **Progress line in the last step of writing**: Writing ⇒ Artifact

#### Finding and citing the tool

> Search engines and AI crawlers had to guess what the site is, and the About page had no DOI.

- robots.txt names the AI crawlers on purpose. They may read the public pages, but not /api/.
- A new /llms.txt says what TraceableAI is and which pages matter.
- The About page shows the tool's DOI and a "Cite this tool" block in APA 7 and BibTeX.
- A gallery report with a DOI is cited by its DOI address, and each version page carries data that says what it is.

#### Updates to the live site

> Every merge took the site offline for a moment and stopped any review that was running.

- **When the live site is updated**: on every merge ⇒ only when a version is published
- An urgent fix can still be put live by hand, and it waits for running reviews first.

### 🐛 Bug fixes

- Fixed a bug where a pilot run's title said "Pilot Pilot".
- Fixed a bug where the self-check skipped a figure such as "F1 0.97".
- Fixed a bug where a source could not fill its cap when records dropped out for their year or as duplicates.
- Fixed a bug where an opened citation's card stayed out of view.
- Fixed a bug where a heading sat alone at the foot of a page.
- Fixed a bug where the report page scrolled sideways at 809 px.
- Fixed a bug where a table's caption was left behind at the foot of a page.

## [0.6.0] - 2026-11-26

The systematic review now answers its research questions first, cites what the studies found, and checks its own paper, so it is honest about how good its evidence is. It tells you when a cited study was retracted, marks a citation it could not confirm with a dagger, and rates how sure each answer is. The paper covers the whole PRISMA 2020 checklist, is written in plain technical English (based on ASD-STE100) and has APA 7 references completed from Crossref, OpenAlex and arXiv. It reads like a journal article on A4 pages. Many prompts and checks changed, so a run can differ from 0.5.0, most of all a run with research questions, and a rerun now gives the same themes. The multivocal review, still in preview, learned a lot from its first real run. And the site now puts every review in one fair line, keeps bots at the door and has a gallery of example reports.

### Added

The review now reads each study with your research questions in mind. It picks the passages to read by the words of your questions, asks for the findings that answer them, and tags each finding with its question. The themes are built the same way, so a code that bears on a question is never dropped. Each finding still needs a quote found word for word in the study. A run without research questions reads the studies as before. A study read before with the same model, questions and text is now taken from the cache, and the methods say how many were.

The answers to the research questions are now written first. The discussion builds on them instead of saying them again. The conclusion and the end of the abstract are written from the checked answers, so the review states its main reading once, not three times in three ways.

You can now edit the finished report on its page. The browser that started the run sees "Edit the text". Click a sentence, rewrite it and press "Check and save". Every citation in it is checked again, and you see the new verdicts with their quotes. An edited sentence gets a small mark, and a panel under the report lists every edit, with the text before and after. You can undo your newest edit of a sentence. The parts written in code, such as the method, the counts and the references, cannot be edited.

The multivocal review now builds out from its most cited academic sources, as the systematic review does. Code picks the ten included papers with the most citations per year, and screens the works they cite and the works that cite them. The method lists the seeds in a table.

The multivocal paper now answers each research question in one place, then discusses, then closes with the limits of the evidence, what it means for practitioners and researchers, and a conclusion. If the discussion cannot be written, the paper says so, and the statement on AI use names only the parts that were.

A multivocal question about a relationship is now answered in code by pairing both sides for each source. If the map holds only one side, the paper says the question cannot be answered. Counts now show the grey sources, the academic sources and both, each with its share.

The multivocal paper now says how many screening decisions were flagged for the reviewer, why, and how many nobody looked at.

While a review runs, a line under its title now says what it is doing right now, such as "Screening record 120 of 431…", with a small count of what was found, screened and included.

Every kind of review now waits in one fair line. One address can have two reviews running and three waiting, and the line holds up to a hundred. A waiting review shows its place and a rough wait, such as "You are 4th in line, about 25 minutes". Closing the page keeps your place, and "Leave the line" gives it back.

The Metrics page now shows how much work each run took: minutes, model calls, retries, tokens and call time. Researchers can download every run's figures as CSV or JSON, with a page that explains each column. The files hold no question, no paper text and no user data. A multivocal run now keeps its figures for the grey and the academic sources apart.

The report now checks itself. When a run ends, ten checks in code read the paper as an editor would. They look for numbers in the abstract that are not the run's counts, a research question without a cited answer, sentences with more than three references and a list of references that does not match the text. The result is kept in self-check.json, and the run page and the report must give the same counts of checked citations.

Every answer now says how sure it is. Each answer to a research question gets a certainty of high, moderate, low or very low, worked out in code from the studies behind it. The rule is inspired by GRADE and GRADE-CERQual, and the method says it is an automated estimate, not a rating made by people. A second check asks whether the answer still holds without its weakest studies. Reviews among the included studies are now treated as secondary studies, and a small search is called a pilot in the title and on the front page.

The review now looks for retractions. Before a study is read, Crossref is asked for any retraction, correction or expression of concern about it. The report says what was found. If the lookup fails, the report says the study was not checked, never that it was clear. A flagged study is named, so you can decide what to do with it.

A citation the check could not confirm now carries a dagger. The sentence stays, so you can see the gap, and a footnote says that the automated check could not confirm it. An edited sentence follows its own new verdict. Each link in a concept map or flow chart is checked as well. A link that no study makes is drawn as the review's own reading.

The paper now covers the whole PRISMA 2020 checklist. The flow figure goes on to the reports sought and not retrieved, and a new part, "What This Review Did Not Assess", says plainly what the tool does not do, such as effect measures and bias across studies. It also has the declarations a journal asks for, with places for the authors to fill in, and an honest statement on the use of AI. The paper no longer claims BTECH's support for anyone's review. main.tex is now proper LaTeX, with labels, cross references and a commented preamble, so it opens cleanly in Overleaf.

References are now in APA 7 form, in the text, the list, BibTeX and RIS. Before the references are numbered, each study with a DOI is looked up at Crossref, then at OpenAlex and arXiv, to fill in authors, volume, issue and pages. A preprint that was later published is cited as the published article. The methods say how many references were completed and which fields were added. No model takes part. The multivocal review completes its academic references the same way.

The report now reads like a journal article. The Review output page shows A4 pages with a front page in journal style, and printing gives the same layout, with even margins. The first page names the type of review, the title and the author line, with a note that an AI system wrote it. Every report page also carries a machine-readable mark that it is AI-generated, as the EU AI Act asks from 2 December 2026. The same mark is in main.tex.

Owners can now add a finished report to a gallery of example reports. A short consent text comes first, and only the browser that started the run sees the button. Every version of a review is kept at its own lasting address, so a citation never points to a changed report. Each card says how the citations held up, shows the self-check, and has a search box and a Cite button. The public copy leaves out publishers' abstracts and long quotes. A review whose card is switched off stays out of the gallery.

The Metrics page now shows what each run cost, worked out from its token counts and Mistral's list prices. The site also has a privacy page that names who is responsible, and an accessibility statement, both linked from every footer.

The report page now shows what the paper found out about itself. A closed line reads, for example, "The paper checked itself: 8 checks passed, 2 notes, 1 fault", and opens a table of the ten checks with the evidence for each. The same line is on the Done card of a finished run and on the printed front page. The front page also gives the result of the retraction check and how many included studies are secondary studies. A run made before the self-check is checked from its files when you open the report. Web addresses in a reference are now links, in the page and in a saved PDF.

An example report now looks like the review output page. It has the same A4 pages, front page, contents list, citation cards and citation panel. It keeps its own address and leaves out the buttons for the run archive and the decision log.

### Changed

The review now cites findings, not its own readings. A sentence that reports what a study found ends with its citation. The review's own reading is a separate sentence with no citation, and code checks that it names no study, number or design. A recommendation or a proposal for future research takes no citation. The rationale ends with one sentence on what is not yet known. When the repair fixes a citation, it knows what the section is for, so it no longer turns a proposal into a finding. In the runs we checked, most flagged citations came from these sections, so expect fewer flags and fewer citations there.

A result figure from a theme may now appear in the abstract and in one place after the results, not in every section. Think of it as one encore, not four. 🎤 The future research and the conclusion use none. A part that repeats a figure is sent back up to twice and then kept, so the check can never cost the run a section.

The peer review now gets the facts. It sees each study's design, which studies sit in each theme and which sit in none. It may state no fact the facts do not give, may not ask a theme to cite a study outside it, and judges a theme on one study by how well it reports that study. With research questions, it also checks that each section serves its question. A theme on one study now needs a reason why that study alone answers part of the aim.

The report is more honest about what it synthesised. The line "The synthesis and discussion cite X of Y" now counts only the themes, the overview and the discussion. The abstract gives the number of studies placed in no theme when it gives the number of included studies.

The scan for text that tries to steer the model now flags only phrases aimed at the model, such as "ignore all previous instructions". A page that only talks about a "system prompt" is still logged but no longer flagged, screened again or counted in the statement on AI use. A review about prompts now gets far fewer false alarms. This holds for both kinds of review.

The multivocal review screens more strictly. A source is kept only when its main subject meets the criteria. The planning form warns when the criteria look broad, and the run warns when more than half of the first thirty sources are kept.

The multivocal review now fetches every page before it calls the model, and a page has 90 seconds to arrive. A site whose robots.txt could not be read is tried again after ten minutes, not a day. Stack Overflow questions are now read through the Stack Exchange API, with their answers, since the site turns page requests away.

Checking quotes is faster. Each source's text is made ready once, not again for every quote. The answers are exactly the same.

The site now guards itself against bots. Pages, downloads and the actions that cost a model call have limits per address, generous enough for a class on one campus network. Uploads to the archive check are capped and handled one at a time. The start forms carry a hidden field that only bots fill in. The privacy page says what is counted and for how long.

The language edit now writes plain technical English (based on ASD-STE100). Sentences aim at 25 words or fewer, a paragraph holds one topic and at most six sentences, and the active voice is the default. It is not a full ASD-STE100 check, and the text is kept at about 80 per cent so that it still reads as natural academic English.

The review now writes in a red thread. A sentence cites at most three references, so the results no longer turn into lists of sources. Large themes are synthesised, and the rationale does not open on a weak source as if it were a fact. A source is weak when it is not empirical, had no text, or fails more than half of its quality criteria. The writer of the fallback text gets the facts, too. The first draft of a section is written only when it is needed, which saves model calls.

Every research question now gets an answer in the paper, even when the closing step fails. Before, a conflict between two rules could send the closing step back until it gave up, and the answers were lost with it. The conclusion may now name kinds of study without a citation.

A rerun on the same studies now gives the same themes. The coding and the codebook are kept in the cache, as the extraction is, and a cached decision is tied to a fingerprint of its prompt. An edit to a prompt can no longer bring back an old decision by mistake. The search and screening are described as they ran. Each search string runs in rounds, and a source counts towards its cap only the records that go on to screening. The paper shows one fingerprint of the protocol.

A quote must now be one run of the paper's own text. Before, a quote could join the end of one sentence to the start of the next, or two parts of a paper joined by an ellipsis, and still count as word for word. Now such a quote is refused. Expect a few more flags, and fewer false comforts.

The report is safer to read and to share. Text that a PDF hides from its reader, such as white text, tiny text or text in a layer that is off, is left out of what the model reads. So is text that a web page hides with a style block, a class or the page background. A PDF is downloaded only from a public address, and each paper is read from its own file. The archive of a run is streamed, so a big run no longer fills the server's memory.

The site holds back when it is not sure. A visitor whose address is unknown now shares one limit with all others. A multivocal run counts as active, so an update waits for it. The app refuses to start on a placeholder key. A null in the model's answer is read as missing, not as a crash, and the code decides what to do.

The multivocal review is more careful. It no longer plans backlink snowballing, which is not built yet, and the form greys that search out. It counts tools one name at a time, and leaves models, languages and methods out of the tool counts. The multivocal paper has even margins and the journal layout.

The review output page has fixes for the citation card, which now closes with a click outside, with Escape or with its button. A table taller than a page now splits across pages.

The header now has four places: New review, Review output, Example reports and Metrics. Example reports needs no run, so a visitor can see a finished report before typing a question. The Review output page with no run behind it says what it will show and offers "Start a review" and "See example reports". The landing page's button opens the chooser, which lets you start only the reviews that are open. The form now has a short line on trust under the Start button, and a phone keeps Example reports in the footer.

### Fixed

Scopus authors are now named the right way round, and a Scopus paper is cited "Liu, J., et al." with the year it went online, not a year in the future. The raw answers of Scopus and IEEE Xplore are now kept with the other databases' answers. A grey source is credited to its real producer, such as the GitHub account or the company behind a blog, and a web address is never an author.

The paper's PDF now prints "<", ">" and "|" as typed, not as "¡", "¿" and "—". A Greek letter or an emoji in a title no longer stops main.tex from compiling. A form no longer fails on half of an emoji.

The token count of a run was always 0, because the model's usage was read the wrong way. It is now right, for both kinds of review.

The multivocal paper now counts its own steps right. Its flow figure adds up, each reason for a paper that could not be rated is given once, a theme with only academic sources no longer claims a grey score of 0, and the paper names the run's own version. The abstract keeps the grey and academic numbers apart. The data appendix names both models, and the call log keeps the reasoning effort when a run is started more than once.

Reference lists are better. A reference no longer cites the journal note of arXiv as an italic venue next to the wrong year. Titles are set in sentence case, and the list is in alphabetical order, as APA asks. A double citation of one study in one sentence is removed, and a repair that changes nothing is no longer counted.

A retraction sentence no longer speaks for studies that were not checked. If some lookups failed, the sentence speaks only for the finished checks and says that the rest are not known.

The cost column in the Metrics downloads was always empty, because no price was set. It is now filled.

The protocol of a multivocal plan no longer promises a search that does not run, so the paper no longer has to explain the gap with a note about software.

Versions 0.5.0 and earlier are in [docs/changelog-archive.md](docs/changelog-archive.md), so that this file stays under 300 lines.

[Unreleased]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.7.0...HEAD
[0.7.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.3...v0.3.0
[0.2.3]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.1.0
