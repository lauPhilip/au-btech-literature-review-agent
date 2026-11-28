"""docs/change-map.json is the "Where to make a change" table of AGENTS.md as data (issue #616).

These tests keep it equal to that table, to the folders git tracks, to what tools/test-scope.sh runs for a change in
each folder, and to the scope table in docs/wiki/07-extending-and-testing.md, so the map is read once and believed
everywhere. They run on every pull request in the "Release check and changelog" job. Standard library only.
"""

import json
import re
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = "AuBtechReviewAgent"
TESTS = "AuBtechReviewAgent.Tests"


def load_map() -> dict:
    return json.loads((ROOT / "docs" / "change-map.json").read_text(encoding="utf-8"))


def table_rows(text: str, header: str) -> list[list[str]]:
    """The cells of each row of the Markdown table under the given header line."""
    lines = text.splitlines()
    start = next(i for i, line in enumerate(lines) if line.strip() == header)
    rows = []
    for line in lines[start + 2:]:
        if not line.startswith("|"):
            break
        rows.append([cell.strip() for cell in line.strip().strip("|").split("|")])
    return rows


def paths_in(cell: str) -> list[str]:
    """Backticked words that are paths, in order."""
    return [word for word in re.findall(r"`([^`]+)`", cell) if "/" in word]


def tracked_folders() -> set[str]:
    out = subprocess.run(["git", "ls-files", APP], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    return {line.split("/")[1] for line in out.splitlines() if line.count("/") >= 2}


def scope(path: str) -> str:
    result = subprocess.run(["bash", "tools/test-scope.sh", "--paths"], cwd=ROOT, input=path + "\n",
                            capture_output=True, text=True)
    if result.returncode != 0:
        raise AssertionError(f"test-scope.sh failed for {path}: {result.stderr}")
    return result.stdout.strip()


def test_classes(folder: str) -> set[str]:
    classes = set()
    for file in (ROOT / TESTS / folder).glob("*.cs"):
        text = file.read_text(encoding="utf-8")
        if "[Fact" in text or "[Theory" in text:
            classes |= set(re.findall(r"^public (?:sealed )?(?:partial )?class (\w+)", text, re.MULTILINE))
    return classes


class ChangeMapTests(unittest.TestCase):

    def setUp(self):
        self.map = load_map()

    def test_areas_match_the_table_in_agents_md(self):
        rows = table_rows((ROOT / "AGENTS.md").read_text(encoding="utf-8"), "| You want to change | Look in |")
        self.assertEqual([row[0] for row in rows], [area["want"] for area in self.map["areas"]],
                         "AGENTS.md and docs/change-map.json list the same wants, in the same order")
        for row, area in zip(rows, self.map["areas"]):
            with self.subTest(want=area["want"]):
                self.assertEqual(paths_in(row[1]), area["look_in"])

    def test_every_area_points_at_known_folders_and_a_wiki_page(self):
        for area in self.map["areas"]:
            with self.subTest(want=area["want"]):
                for path in area["look_in"]:
                    self.assertIn(path.split("/")[0], self.map["folders"])
                    if "<" not in path:
                        self.assertTrue((ROOT / APP / path).exists(), f"{APP}/{path} exists")
                self.assertTrue((ROOT / area["wiki"]).is_file(), f"{area['wiki']} exists")

    def test_folders_are_the_folders_git_tracks(self):
        self.assertEqual(set(self.map["folders"]), tracked_folders(),
                         f"every folder of {APP}/ is in docs/change-map.json, and no other")

    @unittest.skipUnless(shutil.which("bash") and shutil.which("git"), "needs bash and git")
    def test_folders_get_the_tests_test_scope_runs(self):
        module = next(p.name for p in sorted((ROOT / APP / "Modules").iterdir()) if p.is_dir())
        for folder, tests in self.map["folders"].items():
            with self.subTest(folder=folder, tests=tests):
                path = f"{APP}/{folder}/{module}/Example.cs" if tests == "module" else f"{APP}/{folder}/Example.cs"
                answer = scope(path)
                if tests == "all":
                    self.assertEqual(answer, "ALL")
                    continue
                self.assertNotIn(answer, ("ALL", "NONE"))
                picked = set(re.findall(r"FullyQualifiedName~\.(\w+)\.", answer))
                if tests == "module":
                    self.assertTrue(any(name.startswith(module) for name in picked), f"{module} tests run")
                else:
                    self.assertTrue(test_classes(tests) <= picked, f"every test class in {TESTS}/{tests}/ runs")

    def test_wiki_scope_table_agrees(self):
        rows = table_rows((ROOT / "docs/wiki/07-extending-and-testing.md").read_text(encoding="utf-8"),
                          "| Changed file | Tests that run |")
        for folder, tests in self.map["folders"].items():
            with self.subTest(folder=folder, tests=tests):
                key = "`Modules/<Name>/`" if tests == "module" else f"`{folder}/`"
                found = [row[1] for row in rows if key in row[0]]
                if tests == "all" and not found:
                    continue  # "any file not in the map" covers it
                self.assertEqual(len(found), 1, f"one row of the wiki's scope table names {key}")
                if tests == "all":
                    self.assertIn("whole suite", found[0].lower())
                elif tests == "module":
                    self.assertIn("module", found[0])
                elif tests == folder:
                    self.assertIn("same name", found[0])
                else:
                    self.assertIn(f"`{tests}/`", found[0])


if __name__ == "__main__":
    unittest.main()
