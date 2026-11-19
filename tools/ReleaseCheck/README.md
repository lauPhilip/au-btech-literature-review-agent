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

A version with a pre-release part, such as `v0.2.0-rc.1`, is published as a pre-release. The script uses only Python's standard library.

Try it on your own tag before pushing it:

```bash
python3 tools/ReleaseCheck/release_check.py v0.1.0 --notes-out release-notes.md
```

The tests build small git repositories with good and bad tags, and also check the repository's own `CHANGELOG.md` and `CITATION.cff`, including that the `[Unreleased]` section would pass as release notes. They run in CI on every pull request:

```bash
python3 -m unittest discover -s tools/ReleaseCheck -v
```

How to publish a version, step by step, is in [CONTRIBUTING.md](../../CONTRIBUTING.md#publishing-a-version).
