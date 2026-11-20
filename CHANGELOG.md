# Changelog

All notable changes to TraceableAI are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version starts with 0, the method and the output may still change between minor versions.

Each version's section becomes the notes of its GitHub release, so write it for someone deciding which version to use or cite. Under each version, say what was **Added**, **Changed** and **Fixed** in short, plain paragraphs, and what it means for someone running a review. The notes must score at least 60 for Flesch Reading Ease, have exactly one emoji and may be a bit playful; the release check stops the release otherwise. Changes that are merged but not yet released go under [Unreleased]. How to publish a version is in [CONTRIBUTING.md](CONTRIBUTING.md#publishing-a-version).

## [Unreleased]

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

[Unreleased]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.1.0
