"""Tells whether a version tag is the newest version tag, in Semantic Versioning order.

    python3 tools/ReleaseCheck/newest_tag.py v0.2.1

release.yml uses this before it deploys a release: the server is deployed only when the tag just released is the
newest version, so tagging an older version never rolls the server back, and a merge that lands on master while
the release workflow runs does not stop the deploy. It reads the version tags from `git tag --list` in the current
directory (checked out with the whole history), writes deploy=true or deploy=false to $GITHUB_OUTPUT and prints
why. Order follows semver.org rule 11: the numbers first, a pre-release below the release it belongs to
(v0.2.0-rc.1 < v0.2.0), pre-release parts compared one by one, numbers below words and a shorter list below a
longer one it starts with. Build metadata after "+" is ignored. Tags that are not versions are ignored.
Standard library only.
"""

from __future__ import annotations

import os
import subprocess
import sys

from release_check import _SEMVER


def sort_key(tag: str) -> tuple | None:
    """A key that sorts versions in Semantic Versioning order, or None when the tag is not a version."""
    match = _SEMVER.match(tag)
    if not match:
        return None
    major, minor, patch = (int(part) for part in match.group("version").split("+")[0].split("-")[0].split("."))
    pre = match.group("pre")
    if pre is None:
        return (major, minor, patch, 1, ())
    parts = tuple((0, int(p), "") if p.isdigit() else (1, 0, p) for p in pre.split("."))
    return (major, minor, patch, 0, parts)


def is_newest(tag: str, tags: list[str]) -> bool:
    """True when no other version tag sorts above this one. Equal versions (build metadata only) do not count."""
    own = sort_key(tag)
    if own is None:
        return False
    return all(key <= own for key in (sort_key(other) for other in tags) if key is not None)


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if len(args) != 1:
        print(__doc__)
        return 2
    tag = args[0]
    listing = subprocess.run(["git", "tag", "--list", "v*.*.*"], capture_output=True, text=True, check=True)
    tags = listing.stdout.split()
    newest = is_newest(tag, tags)
    if newest:
        print(f"{tag} is the newest version tag; deploying the release.")
    else:
        top = max((t for t in tags if sort_key(t) is not None), key=sort_key, default="none")
        print(f"::notice::Not deployed: {tag} is older than {top}. The server keeps the newer build.")
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as out:
            out.write(f"deploy={'true' if newest else 'false'}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
