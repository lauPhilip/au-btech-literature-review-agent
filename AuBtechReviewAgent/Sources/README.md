# Sources

One class per bibliographic database, all implementing `IAcademicSource`: arXiv, OpenAlex, Semantic Scholar and Crossref need no key; Elsevier (Scopus and ScienceDirect) and IEEE Xplore do. `SourceCatalog.cs` is the list the dashboard offers.

The open sources share `OpenSourceHttp.cs`: one HTTP client, polite retries on rate limits and server errors, and timing of every request, which `SourceStatus.cs` turns into the "arXiv is slow" note on the dashboard and the checks of `/health`. To add a source, see page 7 of the developer wiki.
