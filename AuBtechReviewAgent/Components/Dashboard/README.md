# Components/Dashboard

Parts of the dashboard (`Pages/Home.razor`) that show a run: the run plan before it starts (`RunPlanPreview`), the progress bar and the database status (`RunProgressPanel`, `SourceStatusNote`), the PRISMA funnel (`PrismaFunnel`), the human screening review (`ScreeningReviewPanel`) and the summary of a finished run (`RunSummaryCard`). They only display what the engine has recorded; none of them changes a run on its own. `Pages/` holds only routable pages, and `Layout/` the header, footer and other parts every page shares.
