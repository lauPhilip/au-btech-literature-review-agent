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

from release_notes import MIN_READING_EASE, find_emoji, reading_ease

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
