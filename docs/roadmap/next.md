# What is next

The GitHub issues list is the backlog. Every piece of open work is an issue, and a fresh contributor should start there: [open issues](https://github.com/lauPhilip/au-btech-literature-review-agent/issues). This page only says where to look and records the items that used to sit as open boxes in the old `TODO.MD`.

The plans for the review modules have their own pages. [review-modules.md](review-modules.md) describes the module structure (steps M1 to M9) and [mlr-todo.md](mlr-todo.md) is the step-by-step plan for the multivocal and grey literature review, mapped to Garousi et al. (2019). The redesign is described in [redesign.md](redesign.md). Work that is already done is in the [changelog](../../CHANGELOG.md), and the older record is in [history.md](history.md).

The open items of the old `TODO.MD` were checked against the code and the issues in November 2026. Four are real and are now issues; the rest were done or no longer apply.

| Old item | Status | Where |
|---|---|---|
| Enhanced arXiv adapter (category filter) | Open | [#774](https://github.com/lauPhilip/au-btech-literature-review-agent/issues/774) |
| Run history in a database | Open, needs a decision first | [#775](https://github.com/lauPhilip/au-btech-literature-review-agent/issues/775) |
| Docker image in releases | Open | [#776](https://github.com/lauPhilip/au-btech-literature-review-agent/issues/776) |
| Second model for dual screening | Open | [#777](https://github.com/lauPhilip/au-btech-literature-review-agent/issues/777) |
| IEEE Xplore API | Done: `IeeeXploreSource` is built and offered when a key is set | none |
| Automated citation chaining | Done: backward and forward chaining through OpenAlex, see [#409](https://github.com/lauPhilip/au-btech-literature-review-agent/issues/409) and [#411](https://github.com/lauPhilip/au-btech-literature-review-agent/issues/411) | none |
| Deploy to Simply | Done: the deploy workflow runs on release, see [#707](https://github.com/lauPhilip/au-btech-literature-review-agent/issues/707) | none |
| Live document workspace sync | Dropped: nothing in the code uses a `/PapersWorkspace` folder; each run's archive replaces it | none |
