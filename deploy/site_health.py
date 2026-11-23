"""What the deploy asks the live site before and after it updates it, through GET /health.

deploy.yml runs this in three places (see docs/wiki/06-web-app-and-operations.md, "Deployment"):

    python3 deploy/site_health.py needs-deploy URL HEAD_SHA
        Asks the site which commit it runs and compares it with the commit to deploy. When every file that changed
        between the two is documentation (docs/, or a .md file outside wwwroot), there is nothing to deploy.
        Writes deploy=true or deploy=false to $GITHUB_OUTPUT. When the site does not say, it deploys.

    python3 deploy/site_health.py wait-idle URL --minutes 150
        Waits until no review is running (ActiveRuns is 0), because taking the site offline stops every run.
        After the time limit it gives up waiting and lets the deploy go on: a merged fix must ship. When the site has
        not answered for --down-minutes, nothing is running that could be protected, so it goes on as well.

    python3 deploy/site_health.py wait-started URL COMMIT --minutes 5
        After app_offline.htm is removed: waits until /health answers 200 and names COMMIT. Exits 1 otherwise, so a
        new version that does not start (HTTP 502.5) fails the job instead of going unnoticed.

What each step decided is written to the job summary ($GITHUB_STEP_SUMMARY) when it is set. Standard library only.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Callable


@dataclass(frozen=True)
class Answer:
    """One answer from /health: the HTTP status (0 when there was none) and the JSON, when it was JSON."""
    status: int
    body: dict | None

    @property
    def active_runs(self) -> int | None:
        value = (self.body or {}).get("activeRuns")
        return value if isinstance(value, int) and value >= 0 else None

    @property
    def commit(self) -> str | None:
        value = (self.body or {}).get("commit")
        return value.lower() if isinstance(value, str) and value else None

    def describe(self) -> str:
        if self.status == 0:
            return "no answer"
        if self.body is None:
            return f"HTTP {self.status}, not the health report (the update page or an error page)"
        runs = (self.body.get("runs") or {}) if isinstance(self.body.get("runs"), dict) else {}
        detail = f" ({runs.get('systematic', '?')} systematic, {runs.get('multivocal', '?')} multivocal)" if runs else ""
        return (f"HTTP {self.status}, version {self.body.get('version')}, commit {self.commit or 'not given'}, "
                f"{self.active_runs if self.active_runs is not None else '?'} running review(s){detail}")


def fetch(url: str, timeout: float = 20) -> Answer:
    """GET the health report. Any failure is an answer too: status 0, or a body that is not the report."""
    request = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "traceableai-deploy"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 (the URL is the site's own)
            return Answer(response.status, parse(response.read()))
    except urllib.error.HTTPError as error:
        return Answer(error.code, parse(error.read()))
    except (urllib.error.URLError, OSError, ValueError):
        return Answer(0, None)


def parse(raw: bytes) -> dict | None:
    try:
        body = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return None
    return body if isinstance(body, dict) else None


def is_docs(path: str) -> bool:
    """A file that does not change the site: the docs folder, or a Markdown file that is not served (wwwroot is)."""
    path = path.strip().replace("\\", "/")
    if path.startswith("docs/"):
        return True
    return path.lower().endswith(".md") and "/wwwroot/" not in f"/{path}"


def docs_only(paths: list[str]) -> bool:
    """True only when something changed and all of it is documentation."""
    changed = [p for p in paths if p.strip()]
    return bool(changed) and all(is_docs(p) for p in changed)


def changed_files(base: str, head: str) -> list[str] | None:
    """The files that differ between two commits, or None when git cannot tell (e.g. the commit is not known)."""
    try:
        done = subprocess.run(["git", "diff", "--name-only", base, head], capture_output=True, text=True, check=True)
    except (OSError, subprocess.CalledProcessError):
        return None
    return done.stdout.splitlines()


def needs_deploy(answer: Answer, head: str, diff: Callable[[str, str], list[str] | None] = changed_files) -> tuple[bool, str]:
    live = answer.commit
    if live is None:
        return True, f"The site does not say which commit it runs ({answer.describe()}), so it is deployed."
    paths = diff(live, head)
    if paths is None:
        return True, f"The site runs {live[:7]}, which git cannot compare with {head[:7]}, so it is deployed."
    if docs_only(paths):
        return False, (f"Not deployed: only documentation changed between the site's {live[:7]} and {head[:7]} "
                       f"({len(paths)} file(s)).")
    return True, f"Deploying: {len(paths)} file(s) changed between the site's {live[:7]} and {head[:7]}."


def wait_idle(get: Callable[[], Answer], minutes: float, interval: float, down_minutes: float,
              clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep,
              log: Callable[[str], None] = print) -> tuple[str, str]:
    """Polls until no review runs. Returns (idle | down | timeout, a sentence for the job summary)."""
    start = clock()
    down_since: float | None = None
    while True:
        answer = get()
        now = clock()
        waited = (now - start) / 60
        if answer.active_runs == 0:
            return "idle", f"No review was running after {waited:.0f} min of waiting; the site was taken offline then."
        if answer.active_runs is None:
            down_since = now if down_since is None else down_since
            if (now - down_since) / 60 >= down_minutes:
                return "down", (f"The site gave no health report for {down_minutes:g} min ({answer.describe()}), "
                                "so no running review could be protected; deployed anyway.")
        else:
            down_since = None
        if waited >= minutes:
            return "timeout", (f"Reviews were still running after the {minutes:g} min limit ({answer.describe()}); "
                               "deployed anyway, which stopped them. They can be started again to continue.")
        log(f"Waiting: {answer.describe()}; {waited:.0f} of {minutes:g} min.")
        sleep(interval)


def wait_started(get: Callable[[], Answer], commit: str, minutes: float, interval: float,
                 clock: Callable[[], float] = time.monotonic, sleep: Callable[[float], None] = time.sleep,
                 log: Callable[[str], None] = print) -> tuple[bool, str]:
    """Polls until /health answers 200 and names the commit. Returns (started, a sentence for the job summary)."""
    start = clock()
    want = commit.lower()
    while True:
        answer = get()
        if answer.status == 200 and answer.commit == want:
            return True, f"The new version started: /health answers 200 and names {want[:7]}."
        if (clock() - start) / 60 >= minutes:
            return False, (f"The new version did not start within {minutes:g} min: /health last gave "
                           f"{answer.describe()}, not HTTP 200 with commit {want[:7]}. Look at the site now; "
                           "with HTTP 502.5, turn on stdoutLogEnabled in the server's web.config (see the wiki).")
        log(f"Not started yet: {answer.describe()}.")
        sleep(interval)


def report(name: str, value: str, summary: str) -> None:
    """Writes the step's result to $GITHUB_OUTPUT and its sentence to the job summary, when they are set."""
    print(summary)
    if path := os.environ.get("GITHUB_OUTPUT"):
        with open(path, "a", encoding="utf-8") as out:
            out.write(f"{name}={value}\n")
    if path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(path, "a", encoding="utf-8") as out:
            out.write(summary + "\n\n")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    needs = commands.add_parser("needs-deploy")
    needs.add_argument("url")
    needs.add_argument("head")
    idle = commands.add_parser("wait-idle")
    idle.add_argument("url")
    idle.add_argument("--minutes", type=float, default=150)
    idle.add_argument("--interval", type=float, default=60)
    idle.add_argument("--down-minutes", type=float, default=10)
    started = commands.add_parser("wait-started")
    started.add_argument("url")
    started.add_argument("commit")
    started.add_argument("--minutes", type=float, default=5)
    started.add_argument("--interval", type=float, default=10)
    args = parser.parse_args(argv)
    get = lambda: fetch(args.url)  # noqa: E731

    if args.command == "needs-deploy":
        deploy, summary = needs_deploy(get(), args.head)
        report("deploy", "true" if deploy else "false", summary)
        return 0
    if args.command == "wait-idle":
        result, summary = wait_idle(get, args.minutes, args.interval, args.down_minutes)
        if result != "idle":
            print(f"::warning::{summary}")
        report("idle", result, summary)
        return 0
    ok, summary = wait_started(get, args.commit, args.minutes, args.interval)
    if not ok:
        print(f"::error::{summary}")
    report("started", "true" if ok else "false", summary)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
