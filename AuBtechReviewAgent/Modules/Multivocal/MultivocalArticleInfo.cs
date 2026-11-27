namespace AuBtechReviewAgent;

/// <summary>The front page of a printed multivocal paper (<see cref="ArticleInfo"/>), from the run's files.</summary>
public static class MultivocalArticleInfo
{
    /// <summary>The models that wrote, screened, synthesised and checked the run, each named once; the paper's when there is one.</summary>
    public static IReadOnlyList<string> ModelsOf(MultivocalReportInput? input, MultivocalPaperFile? paper) =>
        AiMarking.Clean(new[] { paper?.Model, input?.Screening.Model, paper?.CheckModel, input?.Synthesis.Model });

    public static ArticleInfo For(MultivocalReportInput input, MultivocalPaperFile paper, bool hasProtocol)
    {
        var plan = input.Planned.Plan;
        var flow = MultivocalPaperParts.Flow(input);
        var models = ModelsOf(input, paper);
        var strings = plan.EffectiveGreySearchStrings.Concat(input.Ledger.HasFormalPool ? plan.EffectiveFormalSearchStrings : Array.Empty<string>());
        return new ArticleInfo
        {
            RunId = input.Planned.RunId,
            Title = paper.Title,
            Abstract = paper.Abstract,
            Kind = plan.UsesFormalPool ? "Multivocal literature review" : "Grey literature review",
            Guideline = "Garousi et al. (2019)",
            ProtocolWrittenUtc = input.Planned.CreatedUtc,
            SearchedUtc = input.Ledger.SearchedUtc == default ? null : input.Ledger.SearchedUtc,
            CompletedUtc = paper.CheckedUtc,
            AppVersion = paper.AppVersion,
            Models = models,
            Found = flow.Found,
            FoundLabel = flow.Found == 1 ? "source found" : "sources found",
            Included = flow.Extracted,
            IncludedLabel = flow.Extracted == 1 ? "source included" : "sources included",
            ProtocolFingerprint = input.Planned.ProtocolSha256,
            Keywords = ArticleKeywords.From(plan.Topic, strings, input.Synthesis.Themes.Select(t => t.Name)),
            ReportPath = $"mlr-paper/{input.Planned.RunId}",
            HasProtocol = hasProtocol,
            // A multivocal run keeps no systematic ledger; its decisions are its screened sources (screened.ris).
            DecisionLogFile = GreyReferences.ScreenedFile,
            DecisionLogTitle = "Every screened source with its decision and the reason, as RIS",
        };
    }
}
