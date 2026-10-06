using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The multivocal review (Garousi et al., 2019), and the grey literature review as its variant with only the grey
/// pool. Not built yet: its cards say "coming soon". The plan is docs/roadmap/mlr-todo.md.
/// </summary>
public sealed class MultivocalModule : IReviewModule
{
    public string Key => "multivocal";
    public string Name => "Multivocal review";
    public string RoutePrefix => "/mlr";

    public IReadOnlyList<ReviewMethod> Cards { get; } = new[]
    {
        new ReviewMethod(
            "multivocal",
            "Multivocal Literature Review",
            "Garousi et al. guidelines",
            "Garousi, Felderer & Mäntylä (2019), Guidelines for including grey literature and conducting multivocal literature reviews, IST 106",
            "https://doi.org/10.1016/j.infsof.2018.09.006",
            "Adds grey literature, such as reports, documentation and practitioner articles, to the academic studies.",
            new[] { "Academic and grey sources", "Quality check for sources that are not peer reviewed", "Every citation checked against the source" },
            Available: false),
        new ReviewMethod(
            "grey",
            "Grey Literature Review",
            "Garousi et al. guidelines",
            "Garousi, Felderer & Mäntylä (2019), Guidelines for including grey literature and conducting multivocal literature reviews, IST 106",
            "https://doi.org/10.1016/j.infsof.2018.09.006",
            "Reviews only grey literature, such as reports, documentation and practitioner articles, where research is scarce.",
            new[] { "Grey sources only", "Quality check for sources that are not peer reviewed", "Every citation checked against the source" },
            Available: false),
    };
}
