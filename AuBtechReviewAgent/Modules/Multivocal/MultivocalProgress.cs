namespace AuBtechReviewAgent;

/// <summary>
/// The steps of a multivocal run as the progress panel shows them, how far a run is, and the state of each step,
/// worked out from the run's stage and the runner's progress line ("12 of 30 sources screened"). The weights are a
/// rough share of the run's time: the quality step fetches every page and is the longest.
/// </summary>
public static class MultivocalProgress
{
    public sealed record ProgressStep(string Key, string Label, int Weight);

    public static readonly IReadOnlyList<ProgressStep> Steps = new[]
    {
        new ProgressStep("search", "Search", 5),
        new ProgressStep("screening", "Screening", 20),
        new ProgressStep("quality", "Quality", 25),
        new ProgressStep("map", "Map", 5),
        new ProgressStep("extraction", "Extraction", 15),
        new ProgressStep("synthesis", "Synthesis", 10),
        new ProgressStep("writing", "Writing", 10),
        new ProgressStep("checking", "Checking", 10),
    };

    /// <summary>The index of the step a run at this stage is on; <see cref="Steps"/>.Count when every step is done.</summary>
    public static int CurrentIndex(string? stage) => stage switch
    {
        MultivocalSearcher.StageSearched or MultivocalScreener.StageScreening => 1,
        MultivocalScreener.StageScreened or MultivocalQualityAssessor.StageAssessing => 2,
        MultivocalQualityAssessor.StageAssessed or MultivocalMapper.StageMapping => 3,
        MultivocalMapper.StageMapped or MultivocalExtractor.StageExtracting => 4,
        MultivocalExtractor.StageExtracted => 5,
        MultivocalSynthesiser.StageSynthesised or MultivocalWriter.StageWriting => 6,
        MultivocalWriter.StageWritten or MultivocalWriter.StageChecking => 7,
        MultivocalWriter.StageChecked => Steps.Count,
        _ => 0,
    };

    /// <summary>The share of a step done, from a progress line such as "12 of 30 sources screened"; 0 without one.</summary>
    public static double Fraction(string? progress)
    {
        var m = System.Text.RegularExpressions.Regex.Match(progress ?? "", @"(\d+) of (\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int done) || !int.TryParse(m.Groups[2].Value, out int all) || all <= 0) return 0;
        return Math.Clamp((double)done / all, 0, 1);
    }

    /// <summary>How far the run is, 0 to 100.</summary>
    public static int Percent(string? stage, string? progress)
    {
        int current = CurrentIndex(stage);
        if (current >= Steps.Count) return 100;
        double done = Steps.Take(current).Sum(s => s.Weight) + Steps[current].Weight * Fraction(progress);
        return (int)Math.Round(done * 100 / Steps.Sum(s => s.Weight));
    }

    /// <summary>The key of the step a run at this stage is on ("screening"), which labels its model calls.</summary>
    public static string KeyFor(string? stage) => Steps[Math.Min(CurrentIndex(stage), Steps.Count - 1)].Key;

    /// <summary>"done", "current" or "waiting" for one step.</summary>
    public static string StateOf(int index, string? stage)
    {
        int current = CurrentIndex(stage);
        return index < current ? "done" : index == current ? "current" : "waiting";
    }
}
