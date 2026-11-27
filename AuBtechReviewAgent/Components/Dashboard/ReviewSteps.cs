using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The seven steps New review shows while a run is going (redesign step 7): Protocol, Search, Screening, Evidence,
/// Writing, Artifact and Checking. Since issue #732 the empty form no longer shows them; they appear when the run starts. A run's own plan
/// (<see cref="RunProgress.Plan"/>) has six to eight steps, because citation chaining and the human screening check
/// are optional. This maps the plan onto the seven: the protocol is written before the first step of the plan,
/// chaining and the human check belong to Screening, full texts and extraction to Evidence, and the last phase of
/// writing (the artifact, <see cref="RunProgress.SynthesisPhases"/>) is the Artifact step.
/// </summary>
public static class ReviewSteps
{
    public const int Count = 7;
    public const int Protocol = 0, Search = 1, Screening = 2, Evidence = 3, Writing = 4, Artifact = 5, Checking = 6;

    /// <summary>Where writing ends and the artifact begins, as a fraction of the synthesis step.</summary>
    public static double ArtifactFrom => RunProgress.SynthesisPhases[^2];

    /// <summary>Title and one-line explanation of each step, in order.</summary>
    public static readonly IReadOnlyList<(string Title, string Line)> All = new[]
    {
        ("Protocol", "Written first, fingerprinted"),
        ("Search", "4 open sources"),
        ("Screening", "Every record twice"),
        ("Evidence", "Full texts read"),
        ("Writing", "Theme by theme"),
        ("Artifact", "From the findings"),
        ("Checking", "Every citation"),
    };

    /// <summary>
    /// The step the run is in: 0 to 6, <see cref="Count"/> when every step is done, and -1 while the run waits for a
    /// free slot (nothing has started, not even the protocol).
    /// </summary>
    public static int Current(ReviewStats stats, string stage)
    {
        if (stats.ProgressStep == RunProgress.Done
            || stage == PrismaReviewEngine.StageComplete
            || stats.ProcessingStage == PrismaReviewEngine.StageComplete) return Count;
        return stats.ProgressStep switch
        {
            RunProgress.Search => Search,
            RunProgress.Screening or RunProgress.Chaining or RunProgress.Review => Screening,
            RunProgress.FullText or RunProgress.Extraction => Evidence,
            RunProgress.Synthesis => stats.ProgressFraction >= ArtifactFrom ? Artifact : Writing,
            RunProgress.Checking => Checking,
            _ => stage == PrismaReviewEngine.StageQueued ? -1 : Protocol,
        };
    }

    /// <summary>
    /// The name of the step the progress line shows ("Screening 40%"). It is the run's own label for the step (such as
    /// "Citation chaining"), except that the last phase of writing is the Artifact step, which the line names too.
    /// </summary>
    public static string Label(ReviewStats stats, string fallback) =>
        stats.ProgressStep == RunProgress.Synthesis && Current(stats, "") == Artifact ? All[Artifact].Title
        : RunProgress.Steps.TryGetValue(stats.ProgressStep, out var d) ? d.Label : fallback;

    /// <summary>"done", "current" or "pending" for step <paramref name="index"/> when the run is in step <paramref name="current"/>.</summary>
    public static string StateOf(int index, int current) =>
        index < current ? "done" : index == current ? "current" : "pending";

    /// <summary>The step number people read, 1 to 7 ("step N of 7").</summary>
    public static int Number(int current) => Math.Clamp(current + 1, 1, Count);

    /// <summary>The one line under a step. Search names how many sources this run asked, when that is known.</summary>
    public static string Line(int index, int sourceCount) =>
        index == Search && sourceCount > 0 ? $"{sourceCount} {(sourceCount == 1 ? "source" : "sources")}" : All[index].Line;
}
