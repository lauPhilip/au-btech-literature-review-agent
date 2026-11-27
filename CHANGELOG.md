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

Version 0.6.0 is in [docs/changelog-0.6.md](docs/changelog-0.6.md), and versions 0.5.0 and earlier are in [docs/changelog-archive.md](docs/changelog-archive.md), so that this file stays under 300 lines.

## [Unreleased]

> **Dev note:** The Decision log button used to hand you a list of screened records. Now it gives you the whole log of the run, every step and why it was taken.
> — The TraceableAI team

### 🌟 Patch highlights

The Decision log on a systematic paper now downloads the run's full ledger as JSON. A server can now choose to call the model in the EU. A systematic paper no longer loses its theme sections when the discussion fails. Citations in the text are now written in one form, such as "[2, 9, 11]" and "Study [9] describes". A systematic paper no longer opens a sentence on a number or writes a theme name with capitals in running text. A book chapter is now cited in its book, not its book series. Otherwise a new systematic run gives the same results as 0.7.0, and a multivocal summary can be worded a little differently.

### 🔬 Systematic review

#### Language model

> Mistral's EU address costs 10% more on every call, so it is a choice for the server, not the default.

- A server can now send its model calls to Mistral's EU address with Llm:MistralBaseUrl, so your text is processed in the EU. It costs 10% more.

#### Discussion

> One failed discussion threw away three finished theme sections, so the paper had no results by theme and no conclusion.

- **Failed discussion**: the themes dropped for a one-pass overview ⇒ the themes kept, and the paper says the discussion is missing
- The discussion gets one more try, so a sentence with too many references is still fixed after two other send-backs. The multivocal discussion gets it too.
- The conclusion, limitations and answers are still written from the themes.
- **Too many references in a discussion sentence**: the whole discussion and conclusion lost ⇒ the sentence split at its clauses, and the sections kept
- A send-back now names every sentence with too many references, not only the first. The closing no longer cites the studies left out of the themes.

#### Rationale and answers

> Run 34d1af9a's rationale began "They use ...", with nothing before it, and its answers built in code read as a list of loose fragments.

- **Rationale opening**: "They use ..." ⇒ the subject named in full. A rationale that opens on "They" or "These" is sent back.
- **A rationale that lists studies**: caught from four cited sentences ⇒ caught from three
- **Answers built in code**: loose fragments ⇒ a first line that says they are not a synthesis, then "Study [3] reports: ..."
- **Certainty summary**: "Of the two answers ..., two are of moderate certainty." ⇒ "Both answers ... are of moderate certainty."

#### Citations in the text

> The paper printed "[9][2][11]", opened a sentence with a bare "[9]", and wrote "No evaluated platform may combine ..." for what one study found.

- **Markers side by side**: "[9][2][11]" ⇒ "[2, 9, 11]", on the page and in main.tex
- A sentence that starts with a marker now names it: "Study [9] describes". A marker stuck to a word gets a space.
- Two more bare markers are named: "[1, 2].[1] proposes" and "evidence, [1] suggests" now read "Study [1] proposes" and "evidence, Study [1] suggests".
- **Hedge rule**: "No platform may combine ..." ⇒ "None of the evaluated platforms combined ...". What a study found in its own sample is stated plainly.

#### Wording

> Run 866a897b put "Architectural and Agent-Level Safety Mechanisms" in running text, opened sentences with "12 studies yielded 36 codes", and cited a study for "abstract-only reports".

- **Theme names**: "Architectural and Agent-Level Safety Mechanisms" in a sentence ⇒ "architectural and agent-level safety mechanisms". The keywords line is lowered as a whole too, and acronyms such as RTW-A stay.
- **Numbers that open a sentence**: "12 studies yielded 36 codes" ⇒ "Of the 16 included studies, 12 yielded 36 codes". The same holds for the abstract and each theme.
- **Empty sentences**: "X is reported [1], and Y is reported [2]." is sent back to the model.
- **Reading basis**: a cited sentence in the rationale or the discussion that says "abstract-only" or "full text" is sent back. The methods text says what the review read.
- The overview of the synthesis in a systematic paper is worded a little differently.

#### References

> Reference [9] had its whole citation in the venue, and a book chapter named its book series where its book should be.

- **A venue that holds a whole citation**: the authors, title, date and place again ⇒ only the proceedings, with the pages
- **A book chapter**: "*Advances in computational intelligence and robotics book series*, 161–182." ⇒ "In *Accelerating logistics through generative AI, ...* (pp. 161–182). IGI Global Scientific Publishing."
- A chapter names the book's editors when its record has them, and BibTeX gets the book, editors and publisher.
- The self-check no longer says an arXiv preprint has no venue.

### 🧭 Multivocal review (preview)

#### Summary for practitioners

> The summary said "as 65 sources describe" next to a practice that those 65 sources did not describe.

- **Counts in the summary**: a count of sources in an item ⇒ no count; the results give the counts
- An item that gives a count is sent back to the model with the count quoted.

#### Tensions between sources

> A tension "between the state of the art and the state of the practice" cited two sources that did not disagree.

- **Sources of a tension**: the first pair the code found ⇒ the two quotes the model named, checked in code
- A tension with no checked pair cites no source, has no pool label, and says so.

### 📄 Report page

#### Paper body

> The paper opened with "The paper checked itself: ...", and a list under the concept map repeated the diagram right below it.

- **Self-check box**: at the top of the paper ⇒ under the report; Article info still gives its one line
- **Cited boxes of the diagram**: a list under the figure ⇒ a table in the appendix, with a one-line pointer under the figure

#### Journal style

> The article front looked like a journal, but the text under it looked like a web page: large sans text, boxed figures and coloured chips.

- **Body text**: 17 px sans at 1.7 ⇒ the abstract's serif at 16 px and 1.55, as wide as the sheet
- **Headings**: Poppins ⇒ the title's serif in navy, with a thin rule under each chapter
- **Figures**: a box with the caption on top ⇒ no box, with the caption under the figure
- **Tables**: the evidence map and the run's table get numbered captions above them, such as "Table 4.1".
- **Citation numbers**: a filled chip ⇒ a plain number in its verdict's colour. It still opens what the check found.
- **Paragraphs on the page**: line breaks inside one block ⇒ one block per paragraph, so a page break falls between lines of a paragraph, not mid-gap.

#### Decision log

> The button said "Decision log" but gave only the screening, so the reasons for every later step were in the archive alone.

- **Decision log**: screened records as RIS ⇒ the run's whole ledger as JSON (ledger.json)
- The printed paper links to the same file. A multivocal paper still gives its screened sources as RIS.

### 🌐 Site

#### Developer wiki

> The wiki lived only in GitHub's Wiki tab, which search engines and AI tools rank poorly.

- **Where the wiki lives**: GitHub's Wiki tab only ⇒ the site's own address too, at /docs, with its diagrams drawn
- Each page links to its source file, and the sitemap and llms.txt list the pages. The Wiki tab still gets a copy.

#### Example reports

> Shared reports were hard to find from outside the site, and a shared link showed only a general line about the tool.

- **Filters**: type and search only ⇒ also the year a report finished and the TraceableAI version it was made with
- **Shared link preview**: a general line about the tool ⇒ the kind of review and its first research question
- The gallery and each shared report are now in the sitemap, so search engines can find them.

### 🐛 Bug fixes

- Fixed a bug where a heading stood alone on a page and its text started on the next one. A paragraph now breaks between two lines when it cannot move whole.
- Fixed a bug where a long paragraph ran over the page footer and out of the page.
- Fixed a bug where a tall concept map ran across the edge of a page. It is now scaled to fit one page.
- Fixed a bug where citations in Table A.1 broke after every letter on a wide screen. They now break between words.
- Fixed a bug where the abstract or conclusion was sent back for a plain "requires" or "must", such as "a study notes that its protocol requires sign-off". Only a claim in the review's own voice is sent back now.
- Fixed a bug where the self-check did not count a figure repeated in a comparison table or a list, only in a diagram.
- Fixed a bug where links in the concept map of main.tex ran through other boxes. Linked boxes now sit side by side, and a long link goes around the boxes.
- Fixed a bug where main.tex left blank space between two figures in the results.
- Fixed a bug where the tab of a multivocal run page did not say when the run had finished while the page was open.

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
- It flags a figure given in two themes or repeated in the concept map, and notes when more than half of the map's links were dashed.

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
- Fixed a bug where the report page scrolled sideways at 809 px.

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
