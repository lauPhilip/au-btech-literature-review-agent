# Changelog

All notable changes to TraceableAI are listed here, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the version starts with 0, the method and the output may still change between minor versions.

Each version's section becomes the notes of its GitHub release, so write it for someone deciding which version to use or cite. Under each version, list what was **Added**, **Changed** and **Fixed**, in plain words. Changes that are merged but not yet released go under [Unreleased]. How to publish a version is in [CONTRIBUTING.md](CONTRIBUTING.md#publishing-a-version).

## [Unreleased]

### Added

- The systematic review (PRISMA 2020): a protocol written before the search, searches of open scholarly databases, screening of every record twice, full-text extraction and quality appraisal, a thematic synthesis, and a citation check that keeps a verdict only when its quote is found word for word in the paper.
- Optional research questions on the start form, listed in the protocol and answered at the start of the conclusion.
- The closing sections of the paper: limitations of the evidence, implications, future research and a conclusion.
- An archive of every run with a SHA-256 manifest, a LaTeX manuscript, BibTeX and RIS files.
- A Metrics page that compares citation quality, coverage and cost across runs.
- Releases titled from the version tag, with notes from this changelog, and a check that `CITATION.cff` names the version being released.

### Changed

- The version comes from the git tag and is shown on the Metrics page and cited in the paper. Runs made before this show "before versioning".
- Screenings and citation checks use a smaller, faster model; the writing uses the large model.

[Unreleased]: https://github.com/lauPhilip/au-btech-literature-review-agent/commits/master
