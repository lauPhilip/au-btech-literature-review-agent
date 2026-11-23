# Changelog

All notable changes to TraceableAI are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version starts with 0, the method and the output may still change between minor versions.

Each version's section becomes the notes of its GitHub release, so write it for someone deciding which version to use or cite. Under each version, say what was **Added**, **Changed** and **Fixed** in short, plain paragraphs, and what it means for someone running a review. The notes must score at least 60 for Flesch Reading Ease, have exactly one emoji and may be a bit playful; the release check stops the release otherwise. Changes that are merged but not yet released go under [Unreleased]. How to publish a version is in [CONTRIBUTING.md](CONTRIBUTING.md#publishing-a-version).

## [Unreleased]

## [0.5.0] - 2026-11-23

TraceableAI has a new look, an API for other tools and a guard against text that tries to steer the model. Many prompts changed, so a run can differ from 0.4.0. A multivocal review, still in preview, now costs far less.

### Added

Other tools can now start a review through an API. Send the form's fields to `/api/v1/reviews` with your own Mistral key, and follow the run by its id. To stop it, send a DELETE with its edit key. Your key is used for that run only and is never stored. The API gives no free reviews, and it stays off on the live site until it is switched on.

The API can also hand back the finished report as data. You get each section, every citation with its verdict and quote, the references and the PRISMA counts. A description of the API is at `/openapi/v1.json`.

AI assistants can run a review too. TraceableAI now has an MCP server at `/mcp`. An assistant can start a review, check on it, read the report and fetch the BibTeX, RIS or LaTeX, all with your own Mistral key. It can also stop a review it started, with the edit key it got back.

The API and the MCP server have their own page, at `/docs/api`. It shows how to start a review, follow it, read the report and stop it, with real examples you can copy. It says what each error means and what to do. Like the API, the page is only there when the API is on.

You can now stop a review you started. The run stops at the next safe point, before its next model call or step. A review that waits in line just leaves the line. The run is marked as cancelled, and the time and your reason are kept on file. Your free review for the day comes back, once a day. Only the browser or tool that started the review can cancel it. Click "Cancel review" beside the run, and the page asks once before it acts. You can say why, if you like. Then the page shows the run as cancelled, with your reason and a button to start a new review. The button works on a phone too.

The multivocal review can now search academic databases as well as the web. It screens papers with the same rules as web sources. It reads a paper's open full text, or its abstract when there is none, and rates it with MMAT, as the systematic review does. Papers and web sources share one map, one set of themes and one paper. Each theme says whether it rests on papers, web sources or both. The paper cites papers as the systematic review does, and it marks a paper read on its abstract alone. The run view shows the two kinds of source side by side. The Multivocal card stays off until a real review has been checked by hand.

The review now looks for text that tries to steer the model. The full text of each included paper, and each kept page of a grey literature review, is checked for phrases that read like instructions to the model, such as "ignore all previous instructions". A finished review lists the papers where it found some, with the phrase, so you can check what was taken from them. Nothing is excluded for it, since papers about language models quote such phrases honestly. The planning form of the grey literature review checks your own answers the same way as the systematic review's form, and asks you to confirm before it starts.

Hidden text is now taken out of a source before it is kept or read by the model. A page or a paper can hide text that you never see but a model does. It can use invisible characters, or a part of a web page that is hidden by its style. That text is removed first, and the run keeps a count of what was removed. A finished review shows which sources lost hidden text. If the hidden text reads like instructions, the source is flagged as well.

The statement on the use of AI now says how many sources were flagged. It counts the sources whose text held phrases that look like instructions to a model. It also says that the flags changed no decision. A run with no flags gets the same statement as before.

The defences now have their own attack test set. It is a small set of poisoned papers and web pages, each with a trick to steer the model, plus a few honest sources as a check. The tests run them through whole reviews with a fake model. They check that every trick is flagged, that no hidden text reaches the model, and that no decision changes. The set found two faults, now fixed. Instructions hidden in invisible tag characters in an abstract were missed at screening. And a paper that only lost a few stray invisible characters was counted as flagged in the statement on AI use.

Citation chaining now looks further out from the studies that matter most. When it is on, code picks the ten included studies with the most citations per year, so a recent study can beat an old classic. For each, it takes the twenty most cited works it cites and the twenty most cited works that cite it, and screens them like any other record. Only studies you already included can be picks, and no model takes part in choosing them. The flow diagram, the methods and a new table in main.tex show what came from them.

The owner of a finished review can now rewrite a sentence of its report, and every citation in that sentence is checked again against its source. A citation that does not hold stays marked, just as in the text the review wrote. Each edit is kept on file with the text before and after, and you can take it back. The parts written in code, such as the method, the counts and the references, cannot be edited. This version adds the engine behind it; the editor on the report page comes next.

### Changed

The theme sections are now written so more citations hold up. Each claim gets its own citation, and sources share one only when each says the whole thing. The writer adds no counts, comparisons or words like "always" that a source does not state.

A repaired paragraph now has to read well, not only be supported. After the repair, each changed paragraph is read once for sense. If it reads badly, it is rewritten once from its supported claims, and every citation in it is checked again. The rewrite stays only if all of them hold. The audit file lists each paragraph and what happened to it.

A repaired sentence in a grey literature review is now checked again as a grey literature sentence, not as part of a systematic review. On a rerun the check comes from the cache.

The rationale in the introduction now keeps only sentences that cite a source. A sentence from the model's general knowledge is left out, and the writing log says which.

The site starts its new look. It now uses a deeper navy, the same one as Coursebot, and two new typefaces: Inter for text and Poppins for headings and buttons. The browser tab shows the new mark, a small robot reading a book. 🤖

The icons that phones and older browsers use now show the same mark on a navy tile. So does the small logo in the header, and an Android phone gets a large icon when you add the site to the home screen.

The top bar is new. It is white, shows the mark next to the name, and has three places: New review, Review output and Metrics. On a phone they fold into a menu. The footer is now one short line with the pages about trust and a link to GitHub.

Every page now sits in one frame. The top bar and the footer stay still, and only the page between them scrolls. A switch in the top bar turns on a dark theme.

The front page now fits on one screen. It says what TraceableAI does in one line and shows a real sentence from a review, with its citations checked one by one: one backed by the paper, one only in part, and one not at all. You can pause it. On a phone the example is left out, so the page fits without scrolling. The front page has its dark theme too.

Starting a review is simpler. The page asks one question, "What should the review find out?", in one box: your objective, your search words and, if you like, up to four research questions. The criteria sit next to it in one panel: years and databases, what to include and leave out, and the picture the review should end with. Fold the panel to one line when you are done with it. On a phone it opens over the page with a Done button. Every field and check from before is still there.

While a review runs, the same page shows its seven steps, from the protocol to the citation check. Each one is ticked off, running or waiting, and the running step says what it is doing. You can close the page; the review keeps going. When it is done, the page opens with what came out: the studies, the themes and how the citations held up, in one coloured bar. One button opens the report, one goes to the citations that need a look, and every file from the run is a click away.

The report is now one calm, long page. It opens with the title, a line on what the review found and one coloured bar for how the citations held up. Every section follows in order, in larger type with shorter lines, so it reads like a paper. A new figure shows how the records flowed from search to the included studies. Each citation is a small coloured chip; open it and a card shows the passage it was checked against. On a laptop a list of sections sits on the left, and on a large screen a panel on the right steps through the citations you choose: all, flagged or abstract only. Printing, notes, re-checks and every download work as before.

The Metrics page now asks one question: how well do the citations hold up? Four figures answer it at the top. Then every review gets its own bar in one chart, oldest on the left, named by the start of its run ID. Three tables follow, each with a total: the share of each verdict per run, the plain counts per run, and the figures for each app version. The detail view of a single run is gone for now; its figures stay in each run's files.

The pages about trust now look alike: About, the model card, privacy, the glossary and the archive check. On a laptop a list on the left takes you between them and to each part of the page you are on. The glossary has a search box and groups its words by method, verdicts and output. Checking an archive is one big box to drop it in, with the result beside it. Choosing the kind of review shows the two you can start as large cards, and a missing page now helps you on your way.

A link to the site now shows a new preview card in the new look. The page you see while a new version goes live has the new look too, in light and dark. The sitemap now tells search engines when each page last changed.

The grey literature review, still in preview, now reads like the systematic one. Its paper is one long page with a list of sections, the coloured bar for how the citations held up and the same panel for stepping through them. Its data appendix and planning page got the new colours too.

Every page now has a dark theme. The switch is in the top bar, and if you have not chosen, the site follows your computer's setting. The citation colours keep their meaning in both themes.

The new look changes nothing about how a review runs. These are the first steps of the redesign; the pages follow one by one.

The citation repair now has a second pass. After the first repair, a citation that is still only partly backed is rewritten once more and checked again. A citation that is still not backed is taken out. If another citation backs the sentence, the sentence stays. If not, the sentence goes too. Each change is kept in citation-audit.json, with the sentence as it was, so nothing goes missing. Both kinds of review use it.

Removed citations are counted on their own. The report says how many there were, and the coloured bar shows them in navy, next to the verdicts. The Metrics page has a column for them in each table, and run-metrics.json keeps the number. The glossary explains what removed means.

The grey literature review now counts removed citations too: in its metrics, the bar on its paper page and its LaTeX note.

On a phone, the main screens now fit without scrolling. A finished review folds the run's files and details into one line that opens with a tap.

Checking an archive now lists every file in it, each marked Matches, Changed or Missing.

Downloads are now smaller on the wire. The reference lists, the protocol, main.tex and the other text files are sent compressed, and so are the site's styles and scripts. Pages load the same, only faster.

Downloads are faster. The archive of a finished review is kept on the server, so a second download sends the same file at once. Before, it was built again each time. A note or a new check of a citation still gives you a fresh archive. Each download now also says when it last changed, so your browser does not fetch a file it already has.

The Metrics page opens faster. The stored figures of past runs are read once and kept in memory, not read again for each visit. A new run still shows at once.

A running review costs the server less. The page that follows a run checked its progress file every second and read it in full each time. Now it reads the file only when it changed.

The research questions now guide every writing step. Each theme says which questions it answers, and a question that no study bears on is named as unanswered, with the reason. The objectives in the paper are now written in code from your plan. Before, only the last step saw the questions, so a theme could drift away from them. A run without research questions is written as before.

The multivocal review now costs far less to run. Screening and the citation checks go to the fast model, as in the systematic review, and the statement on AI use names both models. Each source is screened once. It is screened a second time when the first look kept it, was unsure or found text that looks like instructions, and so is a random sample of at least one in five. Kappa is worked out on that sample, and anyone can draw the sample again from the run's id. A new plan starts with the top 20 hits of each search, not 100. Long pages and papers are sent as their most useful passages, and every quote is still checked against the kept text.

A busy model server no longer ends a run. When the server asks the review to wait, it now waits as long as asked, up to a minute at a time, and tries a few more times. The quick checks also run sixteen at a time instead of eight, so a review is done sooner.

Updating the site no longer stops a review that is running. An update now waits until no review is running, for up to two and a half hours. A change to the docs alone does not update the site at all.

Cards, fields and bars are now square. Only buttons get to keep their round corners.

This release page is new too. It opens with a summary, tells you how to cite the version and folds the long list of pull requests away.

### Fixed

The discussion no longer says the limitations twice. The prompt asks for each part once. If the answer still has two paragraphs on the limitations, the code merges them into one and drops the sentences that only repeat. A note on file says what was dropped.

A grey literature review now counts each tool once. Names that clearly match, such as "MemGPT" and "Letta (MemGPT)", are merged by fixed rules, and the merge is listed in the data appendix. A tool that is one of the review's own sources is not counted as a tool.

The discussion no longer leans on studies the review set aside. When a study fits none of the themes, the later sections now know which one it is and why. Only the limitations may name it. The step that builds the themes now sees the criteria every study met, so it cannot set a study aside for a reason they allow.

The review also stops saying again and again how many studies it read in full. The part on how the review was done says that once. A study is never said to have used the full text as its method, since that is what the review read, not what the study did.

The methods no longer put the model's name in double brackets, such as "(mistral-large-latest (Mistral API), temperature 0)". When the two screenings agree on most records but kappa is still low, the report now says why, so 3 of 4 with a kappa of 0.00 no longer looks like a mistake. The note under Table A.2 explains the dagger only when a value has one, and no longer points to an evidence column the table does not have.

A theme now has one name everywhere: its heading, the overview and the evidence map all use the name from the codebook. The report page and main.tex number their sections the same way, with the same headings and the declarations in the same order, so "see section 5.2" points to the same place in both.

The concept map in main.tex no longer leaves wide gaps between its columns, so it is not shrunk until the text is too small to read. Printing a report, or saving it as a PDF, is now checked on every change: it must run over several pages without the top bar.

A code repository is now cited by the year it was made, not the year of its last change. The last change is still given, as "last updated". Before, every repository was cited as 2026.

A question about how often something happens now gets the same answer in the paper and in the data appendix. Before, the paper counted the answer while the appendix said no theme was found.

The planning form now says which kinds of grey literature each chosen search can find. It warns you when a kind you named has no search that reaches it, and the protocol says the same. Before, a plan could name five kinds and search only GitHub.

The paper of a multivocal review with academic sources now says in its statement on AI use that both pools were screened and appraised, and how each source's citations were checked. Its flow diagram puts each excluded group beside the step where it left. A grey literature review's flow no longer says "1 themes".

Hidden text in your own answers on the planning form is now taken out too. Invisible tag characters could slip into the prompts. Now they are removed, and if they spelled out an instruction, the warning shows you the hidden words.


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

[Unreleased]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.3...v0.3.0
[0.2.3]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.1.0
