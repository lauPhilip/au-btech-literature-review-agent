# Redesign: one calm frame for every page

The redesign gives TraceableAI the simplicity of its sibling Coursebot: one calm landing screen, a fixed app frame with a header and footer that never move, and one task per page. It also brings a new mark, the "reader bot", and a light and a dark theme. The full brief, with every page described at every width, is Lau's redesign handover of 20 November 2026; the brand files and their rules are in `AuBtechReviewAgent/wwwroot/brand/` (`BRAND.md`).

Lau settled two questions before the build started. Cards have a 16 px radius and buttons and fields a 10 px radius, which replaces the old rule that only buttons are rounded. Claude pushes the branches and merges the pull requests once all checks pass, as for every other feature.

## The order of the work

The redesign is built as a series of small pull requests, so the site stays usable after each merge and every step can be checked with screenshots. A page keeps its old look until its own pull request lands; the shared tokens come first so that later pages only use them.

| PR | What it does | Main files |
|---|---|---|
| 1 | Theme tokens as CSS variables with a dark set, the navy `#13294B`, self-hosted Inter and Poppins, rounded cards and fields, the marks, tiles and SVG favicon | `tailwind.config.js`, `Styles/tailwind.input.css`, `wwwroot/fonts/`, `wwwroot/brand/` |
| 2 | The PNG icons from the brand kit replace `apple-touch-icon.png`, `favicon.png`, `favicon.ico` and `img/logo.png` | `wwwroot/` |
| 3 | The frame: `AppLayout`, `LandingLayout`, the new `SiteHeader` and `SiteFooter`, `ThemeToggle` with `wwwroot/js/theme.js`, and `BrandMark` | `Components/Layout/` |
| 4 | Every page moves onto a layout and drops its own header | `Components/Pages/`, `Modules/*/Pages/` |
| 5 | The landing page on one screen, with the looping citation example | `Landing.razor`, `Components/Landing/CitationLoop.razor` |
| 6 | New review, Ask state: the composer and the criteria panel | `Home.razor`, `Components/Dashboard/` |
| 7 | New review, Running and Done states | `RunProgressPanel.razor`, `RunSummaryCard.razor` |
| 8 | Review output as one long report, with the contents list and the citation-check panel | `SpecMatrix.razor`, `Components/Paper/` |
| 9 | Metrics: one stacked bar per run, then the three tables | `Metrics.razor` |
| 10 | Review type, the trust-page template, Verify, Glossary and Not found | `Start.razor`, `About.razor`, `ModelCard.razor`, `Privacy.razor`, `Verify.razor`, `GlossaryPage.razor`, `NotFound.razor` |
| 11 | The multivocal routes adopt the same layouts | `Modules/Multivocal/Pages/` |
| 12 | A last pass at 390, 1440 and 1920 px in light and dark: no scroll bar on phone, the landing and review screens fit 844 px, contrast at least 4.5:1, keyboard order and focus rings intact | all pages |

## Rules that hold throughout

The Content-Security-Policy stays as strict as it is. Fonts are served from `wwwroot/fonts`, and the script that sets the theme before the first paint is a file in `wwwroot/js`, not an inline script. Pages move to the theme-aware colours (`page`, `soft`, `ink`, `muted`, `line`, `primary` and the five verdicts) when their own pull request comes; until then the fixed `brand` navy keeps them looking as before, so dark mode is switched on only once the frame and the pages are ready for it. The print styles keep working, so a report still prints without the frame.

From step 3 the frame exists before the pages use it. The two layouts can be seen on the development pages `/design/frame` and `/design/landing-frame`, and the CI accessibility job checks them in both themes and uploads screenshots of every checked page at 390, 1440 and 1920 px as the workflow artifact `a11y-screenshots`. That artifact replaces screenshots of the live site, which no longer show the markup of a pull request. The theme toggle appears only in the layouts, and a visitor who has not chosen a theme gets the light one until step 12 switches on `ThemeToggle.FollowSystem`.

Four questions stay open and are settled with Lau when their pull request comes: whether Coursebot's own typeface replaces Poppins and Inter, whether Metrics brings back the per-run detail as a view behind each bar, how the multivocal pages look in detail, and whether the phone screens really fit 390 × 844 once they are measured in a browser.
