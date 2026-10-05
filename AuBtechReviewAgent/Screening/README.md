# Screening

The screening stage: deciding for every record whether it meets the criteria. `PrismaReviewEngine.Screening.cs` holds the screening prompt (`ScreenPaperAsync`), the two independent screenings, the peer-review filter and the human review of the decisions. When a screening prompt changes, raise `ScreeningPromptVersion` so older cached decisions are not reused. `ExclusionReasons.cs` groups the exclusions for the funnel and the report, and `ScreeningEvaluation.cs` measures agreement (Cohen's kappa) and accuracy against human decisions.
