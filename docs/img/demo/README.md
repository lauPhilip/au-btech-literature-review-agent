# Demo GIFs

Six clips of six seconds each, for the README, the wiki and talks (issue #259). They are recorded from the app's own sample pages, so no model, key or real run is needed. The Demo GIFs workflow (`.github/workflows/demo-gifs.yml`, script `tools/a11y/record-demos.mjs`) films each scene with Playwright at 1280 × 720 and turns the last six seconds into a GIF with ffmpeg, at 12 frames a second and 960 px wide (720 px for the report and Metrics, which change more of the screen).

To make them again after the pages change, start the workflow by hand in the Actions tab. It pushes the new GIFs to a branch named `demo-gifs/run-<id>`, and a pull request from that branch replaces these files.

| File | What it shows (its alt text) |
|---|---|
| `citation-check.gif` | The landing page example: three citations are checked one by one and marked supported, partly supported and not supported. |
| `new-review.gif` | New review: a question is typed in, then the worked example fills the form, with the criteria beside it. |
| `running.gif` | A running review: its seven steps with the one in progress, and then the finished run with its results. |
| `report.gif` | The report: a citation is opened to show its verdict and the quote from the paper, then the next flagged citation. |
| `metrics.gif` | Metrics: one bar of citation verdicts per review, then the tables of shares and counts per run. |
| `glossary-and-dark.gif` | The glossary is searched for "kappa", and then the site switches to its dark theme. |
