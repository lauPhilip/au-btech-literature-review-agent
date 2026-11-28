# Changelog: version 0.7.0

These are the notes of version 0.7.0, moved here from [CHANGELOG.md](../CHANGELOG.md) so that file stays under 300 lines. Newer versions are in CHANGELOG.md, version 0.6.0 is in [changelog-0.6.md](changelog-0.6.md), and older ones in [the changelog archive](changelog-archive.md).

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
