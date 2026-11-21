using System;
using System.Collections.Generic;

namespace AuBtechReviewAgent;

/// <summary>
/// The seven steps New review shows while a run is going (redesign step 7): Protocol, Search, Screening, Evidence,
/// Writing, Artifact and Checking, the same list as the strip under the composer. A run's own plan
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
    public static IReadOnlyList<(string Title, string Line)> All => Components.Dashboard.ReviewComposer.PipelineSteps;

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

    /// <summary>"done", "current" or "pending" for step <paramref name="index"/> when the run is in step <paramref name="current"/>.</summary>
    public static string StateOf(int index, int current) =>
        index < current ? "done" : index == current ? "current" : "pending";

    /// <summary>The step number people read, 1 to 7 ("step N of 7").</summary>
    public static int Number(int current) => Math.Clamp(current + 1, 1, Count);

    /// <summary>The one line under a step. Search names how many sources this run asked, when that is known.</summary>
    public static string Line(int index, int sourceCount) =>
        index == Search && sourceCount > 0 ? $"{sourceCount} {(sourceCount == 1 ? "source" : "sources")}" : All[index].Line;
}
