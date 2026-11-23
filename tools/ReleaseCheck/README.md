# tools/ReleaseCheck

`release_check.py` is the check that `.github/workflows/release.yml` runs when a version tag is pushed, before anything is published. It reads the annotated tag, `CHANGELOG.md` and `CITATION.cff`, and either prints the release's title and writes its notes, or stops with every reason it found.

| The check | What stops the release |
|---|---|
| The tag | It is not `v` and a Semantic Version (`v0.1.0`, `v0.2.0-rc.1`), or it is a lightweight tag, or its message is empty |
| The title | Taken from the first line of the tag's message: `git tag -a v0.1.0 -m "Research questions"` gives "v0.1.0 - Research questions" |
| `CHANGELOG.md` | It has no section `## [0.1.0]`, or the section is empty. The section becomes the release notes |
| `CITATION.cff` | Its top-level `version` is not `0.1.0`, or its `date-released` is not the day the tag was made |
| Readability | The notes score below 60 for Flesch Reading Ease, which is plain English |
| Emoji | The notes do not have exactly one emoji |

The readability score is 206.835 − 1.015 × (words per sentence) − 84.6 × (syllables per word). Headings, link addresses and code marks are not read, and a list item ends a sentence even without a full stop. Syllables are estimated from groups of vowels, so a score can be a point or two off a dictionary count. An emoji made of several characters, such as a flag or a family, counts once. Whether the notes explain what changed for someone running a review, and are a bit playful, is for the person who reviews the pull request.

The emoji and readability checks live in `release_notes.py`; `release_check.py` reads the tag and the two files and applies them.

`release_body.py` composes the body of the GitHub release, which `release.yml` asks for with `--body-out`. It adds nothing of its own about the version, so the same files give the same body every time.

| Part of the body | Where it comes from |
|---|---|
| The bold summary at the top | The first sentence of the section's lead, the text above its first `###` heading |
| "What this means for your review" | The rest of the lead, then a table counting the paragraphs under each kind, with a fixed line on what each kind means |
| Added, Changed and Fixed | The section, as written |
| "Cite this version" | The concept DOI, title, authors and date in `CITATION.cff`; the version DOI follows once Zenodo has archived the release |
| The folded list of pull requests | GitHub's generated notes since the previous version tag (`--generated-notes`) |
| The comparison link | The previous version tag (`--previous-tag`) and this one |

A version with a pre-release part, such as `v0.2.0-rc.1`, is published as a pre-release. The script uses only Python's standard library.

Try it on your own tag before pushing it:

```bash
python3 tools/ReleaseCheck/release_check.py v0.1.0 --notes-out release-notes.md --body-out release-body.md --previous-tag v0.0.9
```

The tests sit beside the scripts. `test_release_feature.py` builds small git repositories with good and bad tags. `test_release_notes.py` checks the readability score and the emoji count on texts whose answer is known. `test_release_body.py` checks where each part of the release body comes from. `test_release_check.py` checks each rule on its own and also checks the repository's own `CHANGELOG.md` and `CITATION.cff`, including that the `[Unreleased]` section would pass as release notes. `test_workflows.py` checks how the workflows wire the release and the deploy, and `test_site_health.py` checks the deploy's health script. They run in CI on every pull request:

```bash
python3 -m unittest discover -s tools/ReleaseCheck -v
```

How to publish a version, step by step, is in [CONTRIBUTING.md](../../CONTRIBUTING.md#publishing-a-version).
