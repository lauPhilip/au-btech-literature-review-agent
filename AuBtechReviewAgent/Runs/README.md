# Runs

What happens to a run after it has started. `PrismaReviewEngine.Ownership.cs` gives the browser that started a run its edit key, so only that browser can change the run. `PrismaReviewEngine.Notes.cs` keeps the reviewer's notes on citations and studies. `RunMetrics.cs` records each finished run's quality figures for the Metrics page, and `SessionCleanupWorker.cs` deletes runs after the retention period.
