# Changelog

All notable changes to TraceableAI are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version starts with 0, the method and the output may still change between minor versions.

Each version's section becomes the notes of its GitHub release, so write it for someone deciding which version to use or cite. Under each version, say what was **Added**, **Changed** and **Fixed** in short, plain paragraphs, and what it means for someone running a review. The notes must score at least 60 for Flesch Reading Ease, have exactly one emoji and may be a bit playful; the release check stops the release otherwise. Changes that are merged but not yet released go under [Unreleased]. How to publish a version is in [CONTRIBUTING.md](CONTRIBUTING.md#publishing-a-version).

## [Unreleased]

### Changed

The site starts its new look. It now uses a deeper navy, the same one as Coursebot, and two new typefaces: Inter for text and Poppins for headings and buttons. Cards and fields have round corners. The browser tab shows the new mark, a small robot reading a book. 🤖

The icons that phones and older browsers use now show the same mark on a navy tile. So does the small logo in the header, and an Android phone gets a large icon when you add the site to the home screen.

The top bar is new. It is white, shows the mark next to the name, and has three places: New review, Review output and Metrics. On a phone they fold into a menu. The footer is now one short line with the pages about trust and a link to GitHub.

Every page now sits in one frame. The top bar and the footer stay still, and only the page between them scrolls. A switch in the top bar turns on a dark theme.

The front page now fits on one screen. It says what TraceableAI does in one line and shows a real sentence from a review, with its citations checked one by one: one backed by the paper, one only in part, and one not at all. You can pause it. On a phone the example is left out, so the page fits without scrolling. The front page has its dark theme too.

Starting a review is simpler. The page asks one question, "What should the review find out?", in one box: your objective, your search words and, if you like, up to four research questions. The criteria sit next to it in one panel: years and databases, what to include and leave out, and the picture the review should end with. Fold the panel to one line when you are done with it. On a phone it opens over the page with a Done button. Every field and check from before is still there.

While a review runs, the same page shows its seven steps, from the protocol to the citation check. Each one is ticked off, running or waiting, and the running step says what it is doing. You can close the page; the review keeps going. When it is done, the page opens with what came out: the studies, the themes and how the citations held up, in one coloured bar. One button opens the report, one goes to the citations that need a look, and every file from the run is a click away.

The report is now one calm, long page. It opens with the title, a line on what the review found and one coloured bar for how the citations held up. Every section follows in order, in larger type with shorter lines, so it reads like a paper. A new figure shows how the records flowed from search to the included studies. Each citation is a small coloured chip; open it and a card shows the passage it was checked against. On a laptop a list of sections sits on the left, and on a large screen a panel on the right steps through the citations you choose: all, flagged or abstract only. Printing, notes, re-checks and every download work as before.

The Metrics page now asks one question: how well do the citations hold up? Four figures answer it at the top. Then every review gets its own bar in one chart, oldest on the left, named by the start of its run ID. Three tables follow, each with a total: the share of each verdict per run, the plain counts per run, and the figures for each app version. The detail view of a single run is gone for now; its figures stay in each run's files.

The pages about trust now look alike: About, the model card, privacy, the glossary and the archive check. On a laptop a list on the left takes you between them and to each part of the page you are on. The glossary has a search box and groups its words by method, verdicts and output. Checking an archive is one big box to drop it in, with the result beside it. Choosing the kind of review shows the two you can start as large cards, and a missing page now helps you on your way.

The grey literature review, still in preview, now reads like the systematic one. Its paper is one long page with a list of sections, the coloured bar for how the citations held up and the same panel for stepping through them. Its data appendix and planning page got the new colours too.

Every page now has a dark theme. The switch is in the top bar, and if you have not chosen, the site follows your computer's setting. The citation colours keep their meaning in both themes.

Nothing about how a review runs has changed. These are the first steps of the redesign; the pages follow one by one.

### Fixed

The methods no longer put the model's name in double brackets, such as "(mistral-large-latest (Mistral API), temperature 0)". When the two screenings agree on most records but kappa is still low, the report now says why, so 3 of 4 with a kappa of 0.00 no longer looks like a mistake. The note under Table A.2 explains the dagger only when a value has one, and no longer points to an evidence column the table does not have.

A theme now has one name everywhere: its heading, the overview and the evidence map all use the name from the codebook. The report page and main.tex number their sections the same way, with the same headings and the declarations in the same order, so "see section 5.2" points to the same place in both.

The concept map in main.tex no longer leaves wide gaps between its columns, so it is not shrunk until the text is too small to read. Printing a report, or saving it as a PDF, is now checked on every change: it must run over several pages without the top bar.

## [0.4.0] - 2026-11-20

The model writes about the studies from facts, not from its own guesses. This changes what the model is asked, so runs can differ from 0.3.0.

### Fixed

The model now gets each study's design as a fact. Before, it could call two studies one trial, or say two studies were read from the abstract when three were. Each theme now also gets its own counts. 🧮

The limits of the evidence group the designs by type. "Mixed methods case study" and "case study with mixed methods" are no longer two designs. The list is short and right.

## [0.3.0] - 2026-11-20

The data taken from each study is shorter and better backed. This changes what the model is asked, so runs can differ from 0.2.3.

### Fixed

The method and sample of a study are now short, like "24 students", not a whole sentence copied from the paper. A finding keeps its % sign.

A study read in full gets a fair quality check. The model now sees the parts of the paper on how data were collected and studied, and it must answer all five questions. Before, such studies got "can't tell" on every one. 🔍

A study with no abstract and no full text is no longer called "non-empirical". It is marked as not checked, since there was nothing to read.

When a record has no abstract, the screening reason may no longer talk about its abstract.

## [0.2.3] - 2026-11-20

The paper page looks tidier. The method and main.tex are the same as in 0.2.2.

### Fixed

Journal names and volumes on the paper page are now in italics. Before, they showed the stars that mark italics in the source text.

The site now writes numbers with a point, as the paper does. The run page used to say "kappa 0,63" because the server is Danish. ✨

The summary at the top of the run page now lists what rules a study out. It says "None" if you left that field empty.

## [0.2.2] - 2026-11-20

The paper reads better in a few small places. The method is the same as in 0.2.1.

### Fixed

The tables in the appendix of main.tex fit the page again. Table A.3 has a short title, and its key moved to the note below it. Table A.1 lost the empty row under its title.

The methods now say that the limit per source counts the records sent to screening. Before, it read as if it did not match the flow diagram.

A reference that names a journal now gives its volume and issue the APA way. If arXiv knows the journal's DOI, the reference uses it. If the only link is the arXiv one, the reference says it is the preprint. 📐

## [0.2.1] - 2026-11-20

This is a small fix. The paper and the method are the same as in 0.2.0.

### Fixed

The live site now runs the version you see on the Releases page. Before, it was built a moment before the tag, so its papers called a real release "not a published release". Now the site is built again from the tag once the release is out. 🏷️

## [0.2.0] - 2026-11-20

This version makes the paper easier to read and harder to get wrong. We fixed the faults we found in two real runs. 🧹

### Changed

The big tables of the included studies now sit in an appendix after the references. They are Tables A.1, A.2 and A.3, and each keeps its note. Section 3 keeps its text, the flow diagram and the charts, and it tells the reader where to find the tables. This holds for the paper page and for main.tex.

The model now gets the full list of study designs before it writes the discussion. So the discussion uses the same designs as the data table. It also gets every record that was removed before screening, so the counts in the abstract add up to the flow diagram.

The review's own statements no longer carry citations. Counts, the conclusion and the limits are the review's own words, so they do not point to a paper. If a repair makes a citation worse, it is now undone.

### Fixed

Sentences in main.tex end with a full stop again. The funding statement names the right department. The conclusion no longer repeats the answers to the research questions.

A few small slips are gone. The text now says "1 disagreement", not "1 disagreements". A number such as 10^6 is printed as a real power. The list of studies that were not rated ends with "and" in the right place. The source column in the first table uses the same names as the paper page.

The repository now shows its Zenodo DOI, so the software is easy to cite.

## [0.1.0] - 2026-11-19

This is the first version of TraceableAI. It runs a full systematic review, from your question to a paper you can check line by line. 🎉

### Added

You write down your plan before the search starts, so nobody can quietly move the goalposts later. The tool then searches open databases and screens each record twice. It reads the full texts, rates their quality and groups what it finds into themes.

Every claim in the paper points to a source. Each citation is checked against the paper it cites, and it only passes when its quote is found there word for word. If the quote is not there, the claim does not get through.

You can add your own research questions on the start form. The plan lists them, and the conclusion answers them first. The paper also ends with the limits of the evidence, what it means in practice, ideas for future work and a short conclusion.

Each run is saved as an archive with a fingerprint for every file, so you can show that nothing was changed later. You also get the paper in LaTeX, and the references as BibTeX and RIS files for your reference manager.

A new Metrics page puts your runs side by side. It shows how well they cite, how much they cover and what they cost.

Each release now has a clear title and notes like these. A check stops the release if the notes are hard to read, or if the citation file names the wrong version.

### Changed

The version now comes from the release tag. You can see it on the Metrics page, and the paper cites it, so a reader knows which version made the review. Runs from before this show "before versioning".

Screening and citation checks now use a smaller, faster model. The writing still uses the large one, where it counts.

[Unreleased]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.3...v0.3.0
[0.2.3]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.1.0
