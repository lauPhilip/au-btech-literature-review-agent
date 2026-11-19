# Changelog

All notable changes to TraceableAI are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version starts with 0, the method and the output may still change between minor versions.

Each version's section becomes the notes of its GitHub release, so write it for someone deciding which version to use or cite. Under each version, say what was **Added**, **Changed** and **Fixed** in short, plain paragraphs, and what it means for someone running a review. The notes must score at least 60 for Flesch Reading Ease, have exactly one emoji and may be a bit playful; the release check stops the release otherwise. Changes that are merged but not yet released go under [Unreleased]. How to publish a version is in [CONTRIBUTING.md](CONTRIBUTING.md#publishing-a-version).

## [Unreleased]

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

[Unreleased]: https://github.com/lauPhilip/au-btech-literature-review-agent/commits/master
