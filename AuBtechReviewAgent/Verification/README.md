# Verification

Checking that every citation points somewhere real and says what the sentence claims. `CitationValidator.cs` removes citation numbers that are not in the reference list. `CitationSupportChecker.cs` compares each cited sentence with the cited paper and only accepts a verdict whose quote is found word for word in the paper; weak verdicts get a second, independent check. `PrismaReviewEngine.Recheck.cs` checks a single orange or red citation again after the run. The repair of failed citations is in `Synthesis/PrismaReviewEngine.Thematic.cs`, next to the writing it changes.
