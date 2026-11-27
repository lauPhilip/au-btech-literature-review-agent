"""Tests for deploy/site_health.py, which the deploy runs against the live /health. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

The real deploy cannot run in a test, so the unit tests give the waiting loops a scripted list of answers and a
clock that moves only when they sleep, and the feature tests run the script as deploy.yml does, against a small
HTTP server on this machine that plays the site: running reviews, the update page, and a new version starting.
"""

from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "deploy" / "site_health.py"
_spec = importlib.util.spec_from_file_location("site_health", SCRIPT)
site_health = importlib.util.module_from_spec(_spec)
sys.modules["site_health"] = site_health
_spec.loader.exec_module(site_health)
Answer = site_health.Answer

SHA = "40b00a0c59f6b49e121c30d1d6d98ac8f3d3c961"


def report(runs: int, commit: str | None = SHA) -> Answer:
    return Answer(200, {"status": "ok", "version": "0.3.1", "commit": commit, "activeRuns": runs,
                        "runs": {"systematic": runs, "multivocal": 0}})


OFFLINE = Answer(503, None)  # app_offline.htm: HTML, not the report


class Scripted:
    """Gives the answers in order (the last one again and again), and a clock that moves only when it sleeps."""

    def __init__(self, *answers: Answer, step_minutes: float = 1):
        self.answers, self.now, self.step, self.calls = list(answers), 0.0, step_minutes * 60, 0

    def get(self) -> Answer:
        self.calls += 1
        return self.answers.pop(0) if len(self.answers) > 1 else self.answers[0]

    def clock(self) -> float:
        return self.now

    def sleep(self, _seconds: float) -> None:
        self.now += self.step


def idle(s: Scripted, minutes=150, down=10):
    return site_health.wait_idle(s.get, minutes, 60, down, clock=s.clock, sleep=s.sleep, log=lambda _: None)


def started(s: Scripted, minutes=5):
    return site_health.wait_started(s.get, SHA, minutes, 10, clock=s.clock, sleep=s.sleep, log=lambda _: None)


class WaitIdleTests(unittest.TestCase):
    def test_goes_on_at_once_when_nothing_runs(self):
        s = Scripted(report(0))
        self.assertEqual("idle", idle(s)[0])
        self.assertEqual(1, s.calls)

    def test_waits_while_reviews_run(self):
        s = Scripted(report(2), report(1), report(1), report(0))
        self.assertEqual("idle", idle(s)[0])
        self.assertEqual(4, s.calls)

    def test_gives_up_waiting_at_the_limit_and_says_the_runs_were_stopped(self):
        result, summary = idle(Scripted(report(1)), minutes=30)
        self.assertEqual("timeout", result)
        self.assertIn("30 min limit", summary)
        self.assertIn("stopped them", summary)

    def test_goes_on_when_the_site_gives_no_report_for_a_while(self):
        result, summary = idle(Scripted(OFFLINE), down=10)
        self.assertEqual("down", result)
        self.assertIn("no running review could be protected", summary)

    def test_a_short_gap_in_the_answers_does_not_count_as_down(self):
        s = Scripted(report(1), OFFLINE, OFFLINE, report(1), *[OFFLINE] * 5, report(0))
        self.assertEqual("idle", idle(s, down=10)[0])


class WaitStartedTests(unittest.TestCase):
    def test_started_when_health_answers_200_with_the_commit(self):
        s = Scripted(Answer(0, None), OFFLINE, report(0, SHA.upper()))
        self.assertTrue(started(s)[0])

    def test_the_old_version_still_answering_is_not_started(self):
        ok, summary = started(Scripted(report(0, "1" * 40)))
        self.assertFalse(ok)
        self.assertIn("1111111", summary)

    def test_an_app_that_does_not_start_fails_and_says_where_to_look(self):
        ok, summary = started(Scripted(Answer(502, None)))
        self.assertFalse(ok)
        self.assertIn("HTTP 502", summary)
        self.assertIn("stdoutLogEnabled", summary)


class NeedsDeployTests(unittest.TestCase):
    def test_docs_only(self):
        self.assertTrue(site_health.docs_only(["docs/wiki/06-web-app-and-operations.md", "README.md", "AuBtechReviewAgent/Llm/README.md"]))
        self.assertFalse(site_health.docs_only(["docs/wiki/README.md", "AuBtechReviewAgent/Program.cs"]))
        self.assertFalse(site_health.docs_only(["AuBtechReviewAgent/wwwroot/brand/BRAND.md"]))  # served by the site
        self.assertFalse(site_health.docs_only([]))  # the same commit again: a re-run deploys

    def test_compares_the_live_commit_with_the_head(self):
        seen = []
        diff = lambda a, b: seen.append((a, b)) or ["docs/x.md"]  # noqa: E731
        deploy, summary = site_health.needs_deploy(report(0), "f" * 40, diff)
        self.assertFalse(deploy)
        self.assertEqual([(SHA, "f" * 40)], seen)
        self.assertIn("only documentation changed", summary)
        self.assertTrue(site_health.needs_deploy(report(0), "f" * 40, lambda a, b: ["deploy/site_health.py"])[0])

    def test_deploys_when_the_site_or_git_cannot_say(self):
        self.assertTrue(site_health.needs_deploy(OFFLINE, SHA, lambda a, b: ["docs/x.md"])[0])
        self.assertTrue(site_health.needs_deploy(report(0, None), SHA, lambda a, b: ["docs/x.md"])[0])
        self.assertTrue(site_health.needs_deploy(report(0), SHA, lambda a, b: None)[0])


class FakeSite:
    """A local HTTP server that plays /health: each request gets the next (status, body) in the list."""

    def __init__(self, *answers: tuple[int, str]):
        answers_left = list(answers)

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):  # noqa: N802
                status, body = answers_left.pop(0) if len(answers_left) > 1 else answers_left[0]
                data = body.encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def log_message(self, *_):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}/health"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def health(runs: int, commit: str = SHA) -> tuple[int, str]:
    return 200, json.dumps({"status": "ok", "version": "0.3.1", "commit": commit, "activeRuns": runs,
                            "runs": {"systematic": 0, "multivocal": runs}})


class ScriptFeatureTests(unittest.TestCase):
    """Runs the script as deploy.yml does, with a job summary and output file, against the fake site."""

    def run_script(self, site: FakeSite, *args: str) -> tuple[int, str, str, str]:
        with tempfile.TemporaryDirectory() as folder:
            out, summary = Path(folder, "out"), Path(folder, "summary")
            env = {**os.environ, "GITHUB_OUTPUT": str(out), "GITHUB_STEP_SUMMARY": str(summary)}
            done = subprocess.run([sys.executable, str(SCRIPT), args[0], site.url, *args[1:]], env=env,
                                  capture_output=True, text=True, timeout=60, cwd=ROOT)
            site.close()
            return done.returncode, done.stdout, out.read_text(), summary.read_text()

    def test_waits_for_a_running_multivocal_review_then_goes_on(self):
        site = FakeSite(health(1), health(1), health(0))
        code, stdout, out, summary = self.run_script(site, "wait-idle", "--interval", "0.05")
        self.assertEqual((0, "idle=idle\n"), (code, out))
        self.assertIn("0 systematic, 1 multivocal", stdout)
        self.assertIn("No review was running", summary)

    def test_a_timeout_is_a_warning_and_the_deploy_goes_on(self):
        site = FakeSite(health(1))
        code, stdout, out, summary = self.run_script(site, "wait-idle", "--minutes", "0.002", "--interval", "0.05")
        self.assertEqual((0, "idle=timeout\n"), (code, out))
        self.assertIn("::warning::", stdout)
        self.assertIn("deployed anyway", summary)

    def test_the_new_version_starting_after_the_update_page(self):
        site = FakeSite((503, "<html>Updating</html>"), (502, "502.5"), health(0))
        code, _, out, summary = self.run_script(site, "wait-started", SHA, "--interval", "0.05")
        self.assertEqual((0, "started=true\n"), (code, out))
        self.assertIn("The new version started", summary)

    def test_a_version_that_does_not_start_fails_the_job(self):
        site = FakeSite((502, "502.5"))
        code, stdout, out, _ = self.run_script(site, "wait-started", SHA, "--minutes", "0.002", "--interval", "0.05")
        self.assertEqual((1, "started=false\n"), (code, out))
        self.assertIn("::error::", stdout)

    def test_the_same_commit_again_is_deployed(self):
        head = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True, cwd=ROOT).stdout.strip()
        code, _, out, _ = self.run_script(FakeSite(health(0, head)), "needs-deploy", head)
        self.assertEqual((0, "deploy=true\n"), (code, out))  # a re-run of a deploy is not skipped



class DeployWiringTests(unittest.TestCase):
    """deploy.yml asks the site before it goes offline, and again after it starts."""

    def setUp(self):
        self.deploy = (ROOT / ".github" / "workflows" / "deploy.yml").read_text(encoding="utf-8")

    def test_waits_for_running_reviews_before_the_update_page_and_checks_the_start_after(self):
        order = [self.deploy.index(t) for t in ("site_health.py wait-idle", "put app_offline.htm", "rm app_offline.htm",
                                                 "site_health.py wait-started")]
        self.assertEqual(sorted(order), order)

    def test_a_newer_deploy_may_cancel_only_the_waiting(self):
        wait, deploy = self.deploy.split("\n  deploy:\n", 1)
        self.assertIn("group: deploy-simply-wait\n      cancel-in-progress: true", wait)
        self.assertIn("group: deploy-simply\n      cancel-in-progress: false", deploy)
        self.assertIn("needs: wait", deploy)

if __name__ == "__main__":
    unittest.main()
