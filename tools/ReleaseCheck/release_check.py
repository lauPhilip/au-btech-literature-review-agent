"""Checks a version tag before release.yml publishes it, and writes the release's title and notes.

Run from the repository root after the tag is fetched as an annotated tag:

    python3 tools/ReleaseCheck/release_check.py v0.1.0 --notes-out release-notes.md

The release is titled "v0.1.0 - <title>", where the title is the first line of the annotated tag's message
(git tag -a v0.1.0 -m "Research questions"). The notes are the version's section of CHANGELOG.md. The script stops
with every reason it found, and publishes nothing, when:

  * the tag is not "v" followed by a Semantic Versioning 2.0.0 version,
  * the tag is a lightweight tag, or its message is empty,
  * CHANGELOG.md has no section for the version, or the section is empty,
  * CITATION.cff's version or date-released does not match the tag (the date is the day the tag was made),
  * the notes are hard to read: a Flesch Reading Ease score below 60 (plain English),
  * the notes do not have exactly one emoji.

Whether the notes explain what changed for someone running a review, and are a bit playful, is for a person to judge.

When it runs in GitHub Actions it also writes "title" and "prerelease" to $GITHUB_OUTPUT.
Standard library only, so the workflow needs nothing installed.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

# Semantic Versioning 2.0.0, from semver.org, with the "v" prefix MinVer reads (MinVerTagPrefix).
_SEMVER = re.compile(
    r"^v(?P<version>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-(?P<pre>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
    r"(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?)$"
)

# A Keep a Changelog heading: "## [0.1.0] - 2026-11-20", "## [0.1.0]" or "## 0.1.0".
_SECTION = re.compile(r"^##\s+\[?(?P<version>[^\]\s]+)\]?(?:\s+-\s+.*)?\s*$")

# A top-level key in CITATION.cff, written without indentation. Nested keys (under preferred-citation) are indented.
_CFF_KEY = re.compile(r"^(?P<key>[A-Za-z][A-Za-z-]*):\s*(?P<value>.*?)\s*$")


# Release notes must be plain English: a Flesch Reading Ease of 60 or more (60 to 70 is "plain English").
MIN_READING_EASE = 60.0

# Characters that show as an emoji on their own (Emoji_Presentation), outside the main emoji blocks.
_EMOJI_BMP = set(
    "\u231a\u231b\u23e9\u23ea\u23eb\u23ec\u23f0\u23f3\u25fd\u25fe\u2614\u2615\u267f\u2693\u26a1"
    "\u26aa\u26ab\u26bd\u26be\u26c4\u26c5\u26ce\u26d4\u26ea\u26f2\u26f3\u26f5\u26fa\u26fd\u2705"
    "\u270a\u270b\u2728\u274c\u274e\u2753\u2754\u2755\u2757\u2795\u2796\u2797\u27b0\u27bf\u2b1b"
    "\u2b1c\u2b50\u2b55"
) | {chr(c) for c in range(0x2648, 0x2654)}
_VS16, _ZWJ, _KEYCAP = "\ufe0f", "\u200d", "\u20e3"


class ReleaseError(Exception):
    """A reason the release must not be published."""


@dataclass(frozen=True)
class Tag:
    name: str
    is_annotated: bool
    subject: str
    date: str  # YYYY-MM-DD, as the tagger's own clock gave it


@dataclass(frozen=True)
class Release:
    version: str
    title: str
    notes: str
    prerelease: bool


def parse_version(tag: str) -> tuple[str, bool]:
    """Returns the version without the "v", and whether it is a pre-release."""
    match = _SEMVER.match(tag)
    if not match:
        raise ReleaseError(
            f'The tag "{tag}" is not a version. Use "v" and a Semantic Version, such as v0.1.0 or v0.2.0-rc.1.')
    return match.group("version"), match.group("pre") is not None


def release_title(tag: str, subject: str) -> str:
    """'v0.1.0 - Research questions'. A subject that already starts with the tag is not doubled."""
    text = subject.strip()
    text = re.sub(r"^" + re.escape(tag) + r"\s*[-:–]?\s*", "", text).strip()
    if not text:
        raise ReleaseError(
            f'The tag {tag} has no title. Tag it with one: git tag -a {tag} -m "A few plain words".')
    return f"{tag} - {text}"


def changelog_section(changelog: str, version: str) -> str:
    """The text under "## [version]" up to the next "## " heading, without the heading."""
    lines = changelog.splitlines()
    start = None
    for i, line in enumerate(lines):
        match = _SECTION.match(line)
        if match and match.group("version") == version:
            start = i + 1
            break
    if start is None:
        raise ReleaseError(f'CHANGELOG.md has no section "## [{version}]". Add one before tagging.')
    end = len(lines)
    for i in range(start, len(lines)):
        if lines[i].startswith("## "):
            end = i
            break
    # Link definitions at the end of the file ("[0.1.0]: https://...") belong to the file, not the section.
    body = [line for line in lines[start:end] if not re.match(r"^\[[^\]]+\]:\s*\S", line)]
    text = "\n".join(body).strip()
    if not text:
        raise ReleaseError(f"The section for {version} in CHANGELOG.md is empty. Say what was added, changed or fixed.")
    return text


def _is_regional(char: str) -> bool:
    return 0x1F1E6 <= ord(char) <= 0x1F1FF


def _is_skin_tone(char: str) -> bool:
    return 0x1F3FB <= ord(char) <= 0x1F3FF


def _is_pictograph(text: str, i: int) -> bool:
    """True when the character at i starts an emoji: from the emoji blocks, or a symbol shown as one."""
    char = text[i]
    code = ord(char)
    if 0x1F000 <= code <= 0x1FAFF and not _is_skin_tone(char):
        return True
    if char in _EMOJI_BMP:
        return True
    # A text symbol (such as a heart or a warning sign) becomes an emoji when variation selector 16 follows it.
    followed_by_vs16 = i + 1 < len(text) and text[i + 1] == _VS16
    return followed_by_vs16 and (0x2000 <= code <= 0x2BFF or char in "\u00a9\u00ae\u3030\u303d\u3297\u3299")


def find_emoji(text: str) -> list[str]:
    """Every emoji in the text, in order. A sequence that shows as one picture counts once: a family joined with
    zero-width joiners, a flag made of two regional letters, a skin tone, or a keycap such as 1 with its box."""
    found: list[str] = []
    i = 0
    while i < len(text):
        char = text[i]
        if char in "0123456789#*" and text[i + 1:i + 3] in (_VS16 + _KEYCAP, _KEYCAP):
            length = 3 if text[i + 1] == _VS16 else 2
            found.append(text[i:i + length])
            i += length
            continue
        if _is_regional(char):
            length = 2 if i + 1 < len(text) and _is_regional(text[i + 1]) else 1
            found.append(text[i:i + length])
            i += length
            continue
        if not _is_pictograph(text, i):
            i += 1
            continue
        start = i
        i += 1
        while i < len(text):
            if text[i] == _VS16 or _is_skin_tone(text[i]):
                i += 1
            elif text[i] == _ZWJ and i + 1 < len(text) and _is_pictograph(text, i + 1):
                i += 2
            else:
                break
        found.append(text[start:i])
    return found


def _plain_text(markdown: str) -> list[str]:
    """The notes as sentences of plain text. Headings are left out; a list item or a paragraph without a full stop
    still ends a sentence; links keep their words and lose their address; code marks and emphasis go."""
    blocks: list[str] = []
    current: list[str] = []
    for line in markdown.splitlines():
        stripped = line.strip()
        starts_item = re.match(r"^([-*+]|\d+[.)])\s+", stripped)
        if not stripped or stripped.startswith("#") or starts_item:
            if current:
                blocks.append(" ".join(current))
            current = []
            if starts_item:
                current.append(stripped[starts_item.end():])
            continue
        current.append(stripped)
    if current:
        blocks.append(" ".join(current))

    sentences: list[str] = []
    for block in blocks:
        text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", block)
        text = re.sub(r"https?://\S+", " ", text)
        text = re.sub(r"[`*_]", "", text)
        for emoji in find_emoji(text):
            text = text.replace(emoji, " ")
        # A full stop, question or exclamation mark followed by a space ends a sentence; "v0.1.0" does not.
        for sentence in re.split(r"(?<=[.!?])\s+", text):
            if _words(sentence):
                sentences.append(sentence)
    return sentences


def _words(text: str) -> list[str]:
    return re.findall(r"[A-Za-z0-9]+(?:['\u2019.-][A-Za-z0-9]+)*", text)


def count_syllables(word: str) -> int:
    """An estimate of the syllables in an English word: groups of vowels, less a silent final e, at least one.
    A number, or a version such as v0.1.0, counts as one. Parts joined by a hyphen are counted one by one."""
    parts = [part for part in word.split("-") if part]
    if len(parts) > 1:
        return sum(count_syllables(part) for part in parts)
    letters = re.sub(r"[^a-z]", "", word.lower())
    if len(letters) <= 3:
        return 1
    letters = re.sub(r"(?:[^laeiouy]es|[^laeiouy]ed|[^laeiouy]e)$", "", letters)
    letters = re.sub(r"^y", "", letters)
    return max(1, len(re.findall(r"[aeiouy]{1,2}", letters)))


def reading_ease(markdown: str) -> float | None:
    """The Flesch Reading Ease score of the notes, or None when they have no words.
    206.835 - 1.015 x (words per sentence) - 84.6 x (syllables per word). Higher is easier; 60 is plain English."""
    sentences = _plain_text(markdown)
    words = [word for sentence in sentences for word in _words(sentence)]
    if not words:
        return None
    syllables = sum(count_syllables(word) for word in words)
    return 206.835 - 1.015 * (len(words) / len(sentences)) - 84.6 * (syllables / len(words))


def check_notes(notes: str, version: str) -> list[str]:
    """Every way the release notes break the house style. Empty when they keep to it."""
    problems = []
    score = reading_ease(notes)
    if score is None or score < MIN_READING_EASE:
        shown = "no words" if score is None else f"{score:.0f}"
        problems.append(
            f"The notes for {version} are hard to read: Flesch Reading Ease {shown}, and they need at least "
            f"{MIN_READING_EASE:.0f}. Use shorter sentences and everyday words.")
    emoji = find_emoji(notes)
    if len(emoji) != 1:
        shown = "none" if not emoji else f"{len(emoji)} ({' '.join(emoji)})"
        problems.append(f"The notes for {version} need exactly one emoji; they have {shown}.")
    return problems


def citation_fields(citation: str) -> dict[str, str]:
    """The top-level keys of CITATION.cff and their plain values, quotes removed."""
    fields: dict[str, str] = {}
    for line in citation.splitlines():
        match = _CFF_KEY.match(line)
        if match:
            value = match.group("value")
            value = re.sub(r"\s+#.*$", "", value)
            fields[match.group("key")] = value.strip().strip('"').strip("'")
    return fields


def check_citation(citation: str, version: str, date: str) -> list[str]:
    """Every way CITATION.cff disagrees with the tag. Empty when it agrees."""
    fields = citation_fields(citation)
    problems = []
    found_version = fields.get("version")
    if found_version != version:
        shown = f'"{found_version}"' if found_version else "nothing"
        problems.append(f"CITATION.cff gives version {shown}; the tag needs version: {version}.")
    found_date = fields.get("date-released")
    if found_date != date:
        shown = f'"{found_date}"' if found_date else "nothing"
        problems.append(
            f"CITATION.cff gives date-released {shown}; the tag was made on {date}, so it needs date-released: {date}.")
    return problems


def read_tag(tag: str, repo: Path) -> Tag:
    """Reads the tag from git. The tag must exist locally as fetched from the remote."""

    def git(*args: str) -> str:
        result = subprocess.run(["git", *args], cwd=repo, capture_output=True, text=True, check=False)
        if result.returncode != 0:
            raise ReleaseError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
        return result.stdout.strip()

    kind = git("cat-file", "-t", f"refs/tags/{tag}")
    if kind != "tag":
        return Tag(tag, False, "", "")
    subject = git("for-each-ref", "--format=%(contents:subject)", f"refs/tags/{tag}")
    date = git("for-each-ref", "--format=%(taggerdate:short)", f"refs/tags/{tag}")
    return Tag(tag, True, subject, date)


def prepare(tag: Tag, changelog: str | None, citation: str | None) -> Release:
    """Runs every check and raises one ReleaseError listing every reason, or returns the release."""
    problems: list[str] = []
    version, prerelease, title, notes = "", False, "", ""
    try:
        version, prerelease = parse_version(tag.name)
    except ReleaseError as error:
        problems.append(str(error))

    if not tag.is_annotated:
        problems.append(
            f"{tag.name} is a lightweight tag, so it has no title. Delete it and tag again with a message: "
            f'git tag -d {tag.name} && git push origin :refs/tags/{tag.name} && '
            f'git tag -a {tag.name} -m "A few plain words".')
    else:
        try:
            title = release_title(tag.name, tag.subject)
        except ReleaseError as error:
            problems.append(str(error))

    if version:
        if changelog is None:
            problems.append("CHANGELOG.md is missing.")
        else:
            try:
                notes = changelog_section(changelog, version)
                problems.extend(check_notes(notes, version))
            except ReleaseError as error:
                problems.append(str(error))
        if citation is None:
            problems.append("CITATION.cff is missing.")
        elif tag.is_annotated:
            problems.extend(check_citation(citation, version, tag.date))

    if problems:
        raise ReleaseError("\n".join(problems))
    return Release(version, title, notes, prerelease)


def _read(path: Path) -> str | None:
    return path.read_text(encoding="utf-8") if path.is_file() else None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Check a version tag and write its release title and notes.")
    parser.add_argument("tag", help="the tag, such as v0.1.0")
    parser.add_argument("--repo", default=".", help="the repository root (default: the current folder)")
    parser.add_argument("--notes-out", help="write the release notes to this file")
    args = parser.parse_args(argv)

    repo = Path(args.repo)
    try:
        tag = read_tag(args.tag, repo)
        release = prepare(tag, _read(repo / "CHANGELOG.md"), _read(repo / "CITATION.cff"))
    except ReleaseError as error:
        print(f"The release {args.tag} was not published:", file=sys.stderr)
        for line in str(error).splitlines():
            print(f"  - {line}", file=sys.stderr)
            if os.environ.get("GITHUB_ACTIONS") == "true":
                print(f"::error title=Release check::{line}", file=sys.stderr)
        return 1

    if args.notes_out:
        Path(args.notes_out).write_text(release.notes + "\n", encoding="utf-8")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"title={release.title}\n")
            handle.write(f"prerelease={'true' if release.prerelease else 'false'}\n")
    print(release.title)
    return 0


if __name__ == "__main__":
    sys.exit(main())
