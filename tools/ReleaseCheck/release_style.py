"""The patch-notes style that release_check.py holds new release notes to: one emoji on each "### " heading and
none anywhere else, a signed dev note on top, Patch highlights first, Bug fixes last, and "old => new" stat lines.

Released versions up to 0.6.0 keep the old Keep a Changelog form (see uses_patch_notes); this style applies to
[Unreleased] and to every version released from now on. Standard library only.
"""

from __future__ import annotations

import re

from release_notes import find_emoji

# The last version written in the old form. Its notes, and every older version's, are never rewritten.
LAST_OLD_FORM = (0, 6, 0)

# The areas of a patch, in the order they appear, each with the one emoji that marks its "### " heading.
AREAS = [
    ("Patch highlights", "\U0001F31F"),
    ("Systematic review", "\U0001F52C"),
    ("Multivocal review (preview)", "\U0001F9ED"),
    ("Report page", "\U0001F4C4"),
    ("Self-check", "✅"),
    ("Search and sources", "\U0001F50D"),
    ("Site", "\U0001F310"),
    ("Bug fixes", "\U0001F41B"),
]
_EMOJI_OF = dict(AREAS)
_ORDER = [name for name, _ in AREAS]

_SECTION_HEADING = r"^###(?!#)[ \t]+(.+?)[ \t]*$"
_BULLET = re.compile(r"^[-*+]\s+")
_STAT_LINE = re.compile(r"^[-*+]\s+\*\*[^*]+\*\*:\s+\S.*?\S\s+⇒\s+\S.*$")


def uses_patch_notes(version: str) -> bool:
    """True for [Unreleased] and any version after the last one written in the old form."""
    match = re.match(r"^(\d+)\.(\d+)\.(\d+)", version)
    return match is None or tuple(int(part) for part in match.groups()) > LAST_OLD_FORM


def split_section(notes: str) -> tuple[str, list[tuple[str, str]]]:
    """The lead (text above the first "### " heading) and each "### Area" heading with its text, in order."""
    parts = re.split(_SECTION_HEADING, notes, flags=re.M)
    lead = parts[0].strip()
    areas = [(parts[i].strip(), parts[i + 1].strip()) for i in range(1, len(parts), 2)]
    return lead, areas


def heading_name(heading: str) -> str:
    """A heading without its emoji: "\U0001F52C Systematic review" gives "Systematic review"."""
    for emoji in find_emoji(heading):
        heading = heading.replace(emoji, " ")
    return " ".join(heading.split())


def _shown(line: str) -> str:
    return line if len(line) <= 50 else line[:47] + "..."


def _check_emoji(notes: str) -> list[str]:
    problems = []
    for line in notes.splitlines():
        heading = re.match(_SECTION_HEADING, line)
        found = find_emoji(line)
        if not heading:
            if found:
                problems.append(f'The line "{_shown(line.strip())}" has an emoji ({" ".join(found)}); '
                                "only the ### headings carry one.")
            continue
        text = heading.group(1)
        found = find_emoji(text)
        if len(found) != 1 or not text.startswith(found[0]) or not heading_name(text):
            shown = "none" if not found else f"{len(found)} ({' '.join(found)})"
            problems.append(f'The heading "{line.strip()}" needs exactly one emoji, at its start, and a name; '
                            f"it has {shown}.")
    return problems


def _check_shape(notes: str) -> list[str]:
    problems = []
    lead, areas = split_section(notes)
    lines = [line.strip() for line in lead.splitlines() if line.strip()]
    if not lines or not all(line.startswith(">") for line in lines) or not lines[0].startswith("> **Dev note:**"):
        problems.append('The text above the first ### heading must be a blockquote that starts with "> **Dev note:**".')
    names = [heading_name(heading) for heading, _ in areas]
    if not names or names[0] != "Patch highlights":
        problems.append('The first ### heading must be "Patch highlights".')
    last = -1
    for index, ((heading, text), name) in enumerate(zip(areas, names)):
        if name not in _EMOJI_OF:
            problems.append(f'The heading "### {heading}" is not an area. Use one of: {", ".join(_ORDER)}.')
            continue
        if len(find_emoji(heading)) == 1 and _EMOJI_OF[name] not in find_emoji(heading):
            problems.append(f'The heading "### {heading}" needs the emoji {_EMOJI_OF[name]} for {name}.')
        if _ORDER.index(name) < last:
            problems.append(f'The area "{name}" is out of order. The order is: {", ".join(_ORDER)}.')
        last = max(last, _ORDER.index(name))
        if name == "Bug fixes":
            if index != len(areas) - 1:
                problems.append("Bug fixes must be the last area.")
            problems.extend(_check_fixes(text))
    return problems


def _check_fixes(text: str) -> list[str]:
    items = [line for line in text.splitlines() if _BULLET.match(line.strip())]
    loose = [line for line in text.splitlines() if line.strip() and not line[0].isspace() and not _BULLET.match(line)]
    problems = []
    if not items or loose:
        problems.append("Bug fixes must be a list, one item per fix.")
    for item in items:
        if not _BULLET.sub("", item.strip()).startswith("Fixed"):
            problems.append(f'The bug fix "{_shown(item.strip())}" must start with "Fixed".')
    return problems


def _check_stat_lines(notes: str) -> list[str]:
    problems = []
    for line in notes.splitlines():
        if "⇒" in line and not (_STAT_LINE.match(line.strip()) and line.count("⇒") == 1):
            problems.append(f'The line "{_shown(line.strip())}" has an arrow but not the shape '
                            '"- **label**: old ⇒ new" with one arrow and a value on each side.')
    return problems


def check_style(notes: str) -> list[str]:
    """Every way the notes break the patch-notes style. Empty when they keep to it."""
    return _check_emoji(notes) + _check_shape(notes) + _check_stat_lines(notes)
