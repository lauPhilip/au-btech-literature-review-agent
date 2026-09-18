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

Optional API keys, if you want to search those sources: Elsevier/ScienceDirect, IEEE Xplore, and Google Scholar.

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

Optional keys use the same pattern: `dotnet user-secrets set "ELSEVIER_API_KEY" "..."`, and likewise `IEEE_API_KEY` and `SCHOLAR_API_KEY`.

**A note on secrets:** never commit API keys. Use `dotnet user-secrets` (as above) or environment variables so keys stay off the repo. If you think you've committed a key by accident, let a maintainer know so it can be rotated.

## Making a change

1. Create a branch for your work: `git checkout -b short-description-of-change`
2. Make your change. Try to keep each pull request focused on one thing — it's much easier to review a small, clear change than a large mixed one.
3. Build the project and run the tests before submitting, to make sure it still compiles and nothing broke:
   ```bash
   dotnet build
   dotnet test
   ```
   If you're changing or adding behaviour, please add or update a test for it where it makes sense. The same tests run automatically on your pull request.
4. Commit with a short, clear message that says what the change does and why.

## Submitting a pull request

1. Push your branch to your fork.
2. Open a pull request against the `master` branch of this repo.
3. Fill in the pull request template — a short description of what you changed and why is enough.
4. A maintainer will review it, maybe ask a question or two, and merge it once it's ready.

Don't worry about getting everything perfect. If something needs adjusting, we'll talk it through in the review.

## Reporting a bug

Open an issue using the bug report template and include:

- What you expected to happen
- What actually happened
- The steps to reproduce it
- Anything useful like error messages, your OS, and your .NET version

The more detail you give, the faster it can be sorted out.

## Questions

If you're not sure whether something is a bug, whether a feature fits the project, or how to get started, open an issue and ask. We're happy to help.
