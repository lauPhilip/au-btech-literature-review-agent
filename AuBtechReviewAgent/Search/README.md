# Search

The search stage: turning the question into search strings, querying the sources and deciding which records go on to screening. `PrismaReviewEngine.SearchStrings.cs` proposes or takes the reviewer's search strings. `PrismaReviewEngine.Search.cs` runs them against every source, saves every raw answer with its fingerprint, and removes duplicates, records outside the year range and records over the cap (each with its reason). `PrismaReviewEngine.Chaining.cs` runs citation chaining. `SearchSaturation.cs` counts how many new records each search string added.
