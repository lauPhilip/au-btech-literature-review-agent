# Contributing to TraceableAI

Thanks for your interest in helping out. TraceableAI is open source, and contributions of all kinds are welcome — bug reports, ideas, documentation fixes, and code.

This guide explains how to get set up and how to send changes our way. If anything here is unclear, feel free to open an issue and ask.

By taking part in this project, you agree to follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to contribute

You don't have to write code to be useful:

- **Report a bug.** If something breaks or behaves oddly, open an issue and tell us what happened.
- **Suggest an improvement.** Have an idea for a feature or a better way of doing something? Open an issue and describe it.
- **Improve the docs.** Spotted a typo, an out-of-date instruction, or something confusing in the README? Fixes are very welcome.
- **Write code.** Fix a bug or build a feature and send a pull request.

## Setting up the project locally

You'll need:

- The [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- A [Mistral API key](https://mistral.ai/) (required — the app won't start without one)

Optional API keys, if you want to search those sources: Elsevier/ScienceDirect and IEEE Xplore. arXiv, OpenAlex, Semantic Scholar and Crossref need no key. Instead of a Mistral key you can use a local model through Ollama (see "Running without Mistral" in the README).

Then:

```bash
# 1. Fork the repo on GitHub, then clone your fork
git clone https://github.com/<your-username>/au-btech-literature-review-agent.git
cd au-btech-literature-review-agent

# 2. Restore dependencies
dotnet restore

# 3. Add your Mistral key (don't commit it - see the note below)
cd AuBtechReviewAgent
dotnet user-secrets set "MISTRAL_API_KEY" "your-key-here"

# 4. Run the app
dotnet run
```

The app will print a local URL you can open in your browser.

Optional keys use the same pattern: `dotnet user-secrets set "ELSEVIER_API_KEY" "..."`, and likewise `IEEE_API_KEY`. Set a contact e-mail for the open sources with `dotnet user-secrets set "OpenSources:ContactEmail" "you@example.org"`.

If you change the Tailwind classes in a component, rebuild the CSS in the `AuBtechReviewAgent` folder with `npm ci` and `npm run build:css`, and commit `wwwroot/css/tailwind.css` with your change. Never build a class name from a variable (write `bg-red-50`, not `"bg-" + colour + "-50"`), because the build only finds class names written out in full.

**A note on secrets:** never commit API keys. Use `dotnet user-secrets` (as above) or environment variables so keys stay off the repo. If you think you've committed a key by accident, let a maintainer know so it can be rotated.

## Making a change

1. Create a branch for your work: `git checkout -b short-description-of-change`
2. Make your change. [ARCHITECTURE.md](ARCHITECTURE.md) shows which folder each part of the review lives in; put new code in the folder of its stage and its tests in the matching test folder. Try to keep each pull request focused on one thing — it's much easier to review a small, clear change than a large mixed one.
3. Build the project and run the tests before submitting, to make sure it still compiles and nothing broke:
   ```bash
   dotnet build
   dotnet test
   ```
   While you work, you can run only the tests your change can affect, which is much faster:
   ```bash
   tools/test-scope.sh --run            # on Windows: .\tools\test-scope.ps1 --run
   ```
   It compares your branch and your uncommitted files with `origin/master`, prints which tests it picked and why, and runs them; without `--run` it only prints the filter. Run the whole `dotnet test` once before you submit.
   If you're changing or adding behaviour, please add or update a test for it where it makes sense. Your pull request runs the tests its change can affect, and master runs them all.
4. Commit with a short, clear message that says what the change does and why.

## Submitting a pull request

1. Push your branch to your fork.
2. Open a pull request against the `master` branch of this repo.
3. Fill in the pull request template — a short description of what you changed and why is enough.
4. A maintainer will review it, maybe ask a question or two, and merge it once it's ready.

Don't worry about getting everything perfect. If something needs adjusting, we'll talk it through in the review.

## Publishing a version

This part is for maintainers. A version is set in one place only, a git tag such as `v0.1.0`, and the app, the paper and the Metrics page read it from there. Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html): while they start with 0, a new minor version may change the method or the output.

Pushing the tag starts `.github/workflows/release.yml`. It checks the tag, `CHANGELOG.md` and `CITATION.cff`, runs the tests and publishes a GitHub release titled "v0.1.0 - Research questions". The notes are built from the version's section of `CHANGELOG.md`, so write that section with the release page in mind. Its first paragraph, above `### Added`, is the lead: its first sentence becomes the bold summary at the top of the release, and the rest of it opens "What this means for your review", so say there whether runs can differ from the last version. A table of how many changes of each kind follows, then the Added, Changed and Fixed parts as you wrote them. After them come a "Cite this version" part taken from `CITATION.cff`, GitHub's list of merged pull requests folded away, and a link comparing it with the previous release. `tools/ReleaseCheck/release_body.py` composes it the same way every time. Zenodo then archives the release and gives it its own version DOI, with nothing to do by hand. v0.1.0 is [10.5281/zenodo.24379095](https://doi.org/10.5281/zenodo.24379095). The concept DOI, [10.5281/zenodo.24379094](https://doi.org/10.5281/zenodo.24379094), stands for all versions and always opens the newest, so it is the one in the README badge and in `CITATION.cff` and does not change. Cite a version DOI when you need the exact version a run used.

| Step | What to do |
|---|---|
| 1. Prepare | Check that the `[Unreleased]` notes read well: plain English that says what changed for someone running a review, a Flesch Reading Ease of at least 60, exactly one emoji and a bit of play. CI already checks the score and the emoji. Then, on a branch, rename `## [Unreleased]` in `CHANGELOG.md` to `## [0.1.0] - 2026-11-20` and start a new, empty `## [Unreleased]` above it. In `CITATION.cff`, set `version: "0.1.0"` and `date-released: 2026-11-20`. Commit both in one `(docs): ` commit. |
| 2. Merge | Open a pull request and merge it once the checks pass. The "Release check and changelog" job tests both files. |
| 3. Tag | On the merged master, make an annotated tag whose message is the title, a few plain words: `git tag -a v0.1.0 -m "Research questions"`. Tag on the day you wrote in `date-released`. |
| 4. Push the tag | `git push origin v0.1.0`. Follow the Release workflow under Actions. Once the release is published, it deploys the tagged build to the server, so the server and its papers name the release. It deploys only if the tag is on master's newest commit; otherwise it says so and the server keeps the newer build. |

The workflow publishes nothing, and says why, if the tag is not a version, if it is a lightweight tag without a message, if `CHANGELOG.md` has no section for the version, if `CITATION.cff` names another version or day, or if the notes score below 60 or do not have exactly one emoji. To try again, fix the files on master, delete the tag (`git tag -d v0.1.0` and `git push origin :refs/tags/v0.1.0`) and tag again. You can run the same check yourself before pushing: `python3 tools/ReleaseCheck/release_check.py v0.1.0`.

## Reporting a bug

Open an issue using the bug report template and include:

- What you expected to happen
- What actually happened
- The steps to reproduce it
- Anything useful like error messages, your OS, and your .NET version

The more detail you give, the faster it can be sorted out.

## Questions

If you're not sure whether something is a bug, whether a feature fits the project, or how to get started, open an issue and ask. We're happy to help.
