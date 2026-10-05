# AuBtechReviewAgent.Tests

The tests use the same folders as the code they test, so the tests for screening are in `Screening/` and the code in `AuBtechReviewAgent/Screening/`. `Pipeline/` holds the end-to-end runs. Every test runs offline: `Support/FakeChatService.cs` stands in for the model and answers from a script, and the bibliographic sources are faked in `Pipeline/PipelineTests.cs`. Run them all with `dotnet test`.
