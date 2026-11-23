"""Composes the body of a GitHub release from the version's CHANGELOG.md section, CITATION.cff and git.

release_check.py checks the release and calls compose_body; release.yml publishes what it returns. Nothing in the
body is written by a model or made up by the workflow: every sentence about the version comes from CHANGELOG.md, the
citation from CITATION.cff, the list of pull requests from GitHub's generated notes and the link from the two tags.

The body reads, top to bottom:

  * the summary: the first sentence of the lead paragraph, the text above the first "### " heading,
  * "What this means for your review": the rest of the lead, then a table of how many changes of each kind,
  * the Added, Changed and Fixed sections, as written in CHANGELOG.md,
  * "Cite this version": the concept DOI from CITATION.cff, a reference and a BibTeX entry,
  * GitHub's list of merged pull requests, folded in a <details> block,
  * the link that compares the version with the one before.

Standard library only.
"""

from __future__ import annotations

import re

# What each kind of change means for someone running a review. Fixed wording, not about any one version.
KIND_MEANING = {
    "Added": "New things you can use.",
    "Changed": "Works differently now, so a new run can differ from the last.",
    "Fixed": "Wrong before, right now.",
    "Removed": "Gone, so check you do not rely on it.",
    "Deprecated": "Still there, but going soon.",
    "Security": "Safer to run.",
}

_TOP_KEY = re.compile(r"^(?P<key>[A-Za-z][A-Za-z-]*):\s*(?P<value>.*?)\s*$")


def split_section(notes: str) -> tuple[str, list[tuple[str, str]]]:
    """The lead (text above the first "### ") and each "### Kind" with its text, in order."""
    parts = re.split(r"^###\s+(.+?)\s*$", notes, flags=re.M)
    lead = parts[0].strip()
    kinds = [(parts[i].strip(), parts[i + 1].strip()) for i in range(1, len(parts), 2)]
    return lead, kinds


def first_sentence(text: str) -> tuple[str, str]:
    """The first sentence of the text and what follows it. "v0.1.0" or "e.g." mid-word do not end a sentence."""
    match = re.search(r"(?<=[.!?])\s+(?=[A-Z0-9\"'(])", text)
    if not match:
        return text.strip(), ""
    return text[:match.start()].strip(), text[match.end():].strip()


def paragraphs(text: str) -> int:
    """How many changes a kind has: its paragraphs, or its list items when it is a list."""
    items = re.findall(r"^\s*(?:[-*+]|\d+[.)])\s+\S", text, flags=re.M)
    if items:
        return len(items)
    return len([block for block in re.split(r"\n\s*\n", text) if block.strip()])


def citation_authors(citation: str) -> list[tuple[str, str]]:
    """(family, given) for each author in CITATION.cff's top-level "authors:" list."""
    authors: list[tuple[str, str]] = []
    inside = False
    current: dict[str, str] | None = None
    for line in citation.splitlines():
        if not line.startswith(" ") and line.strip():
            inside = line.startswith("authors:")
            continue
        if not inside:
            continue
        item = re.match(r"^\s*-\s+(.*)$", line)
        if item:
            current = {}
            authors.append(("", ""))
            line = item.group(1)
        field = re.match(r"^\s*(family-names|given-names):\s*(.*?)\s*$", line)
        if field and current is not None:
            current[field.group(1)] = field.group(2).strip('"').strip("'")
            authors[-1] = (current.get("family-names", ""), current.get("given-names", ""))
    return [author for author in authors if any(author)]


def citation_field(citation: str, key: str) -> str:
    for line in citation.splitlines():
        match = _TOP_KEY.match(line)
        if match and match.group("key") == key:
            return re.sub(r"\s+#.*$", "", match.group("value")).strip().strip('"').strip("'")
    return ""


def _apa_name(family: str, given: str) -> str:
    if not family:
        return given
    initials = " ".join(f"{part[0]}." for part in given.split() if part)
    return f"{family}, {initials}" if initials else family


def _apa_authors(names: list[str]) -> str:
    if len(names) <= 1:
        return "".join(names)
    return ", ".join(names[:-1]) + ", & " + names[-1]


def cite_block(citation: str, version: str) -> str:
    """The "Cite this version" part: why the concept DOI, a reference and a BibTeX entry."""
    doi = citation_field(citation, "doi")
    title = citation_field(citation, "title")
    year = citation_field(citation, "date-released")[:4]
    authors = citation_authors(citation)
    url = f"https://doi.org/{doi}"
    apa = _apa_authors([_apa_name(family, given) for family, given in authors])
    apa = apa if apa.endswith(".") else apa + "."
    bib_authors = " and ".join(f"{family}, {given}" if family else given for family, given in authors)
    key = "traceableai_" + re.sub(r"[^0-9A-Za-z]+", "_", version)
    return "\n".join([
        "## Cite this version",
        "",
        "Zenodo archives this release and gives it its own version DOI. It follows a few minutes after the release, "
        f"under Versions on the [Zenodo record]({url}). Cite that DOI when you need the exact version a review used. "
        f"Until then, or when any version will do, cite the concept DOI, [{doi}]({url}). It always opens the newest "
        "version.",
        "",
        f"> {apa} ({year}). *{title}* (Version {version}) [Computer software]. Zenodo. {url}",
        "",
        "```bibtex",
        f"@software{{{key},",
        f"  author  = {{{bib_authors}}},",
        f"  title   = {{{title}}},",
        f"  version = {{{version}}},",
        f"  year    = {{{year}}},",
        f"  doi     = {{{doi}}},",
        f"  url     = {{{url}}}",
        "}",
        "```",
    ])


def pull_request_block(generated: str | None) -> str:
    """GitHub's generated list of merged pull requests, folded. Empty when there is none."""
    if not generated:
        return ""
    lines = []
    for line in generated.strip().splitlines():
        if line.startswith("**Full Changelog**") or re.match(r"^##\s+What's Changed\s*$", line):
            continue
        heading = re.match(r"^#{1,6}\s+(.+?)\s*$", line)
        lines.append(f"**{heading.group(1)}**" if heading else line)
    text = "\n".join(lines).strip()
    count = len(re.findall(r"^\*\s+", text, flags=re.M))
    if not count:
        return ""
    noun = "pull request" if count == 1 else "pull requests"
    return f"<details>\n<summary>The {count} {noun} in this version</summary>\n\n{text}\n\n</details>"


def compose_body(tag: str, version: str, notes: str, citation: str, repository: str,
                 previous_tag: str | None = None, generated: str | None = None) -> str:
    """The release body. Deterministic: the same inputs give the same text."""
    lead, kinds = split_section(notes)
    summary, rest = first_sentence(lead) if lead else ("", "")
    blocks: list[str] = []
    if summary:
        blocks.append(f"**{summary}**")
    meaning = [rest] if rest else []
    known = [(kind, text) for kind, text in kinds if kind in KIND_MEANING]
    if known:
        table = ["| Kind of change | How many | What it means |", "|---|---|---|"]
        table += [f"| [{kind}](#{kind.lower()}) | {paragraphs(text)} | {KIND_MEANING[kind]} |" for kind, text in known]
        meaning.append("\n".join(table))
    if meaning:
        blocks.append("## What this means for your review\n\n" + "\n\n".join(meaning))
    blocks += [f"## {kind}\n\n{text}" for kind, text in kinds]
    if not kinds and lead and not summary:
        blocks.append(lead)
    blocks.append(cite_block(citation, version))
    pulls = pull_request_block(generated)
    if pulls:
        blocks.append(pulls)
    if previous_tag:
        compare = f"https://github.com/{repository}/compare/{previous_tag}...{tag}"
        blocks.append(f"Everything that changed since {previous_tag}: [{previous_tag}...{tag}]({compare})")
    return "\n\n".join(blocks) + "\n"
