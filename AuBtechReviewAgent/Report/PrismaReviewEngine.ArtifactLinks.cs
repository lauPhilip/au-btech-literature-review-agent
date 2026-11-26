using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

/// <summary>The artifact after the citation check: its checked texts put back, its links checked and the diagram drawn.</summary>
public partial class PrismaReviewEngine
{
    /// <summary>
    /// Puts the checked (or repaired) texts back into the artifact and drops what the repair deleted, then checks each
    /// remaining link against the studies both of its boxes cite (issue #571), writes artifact-links.json and draws the
    /// diagram. The links are checked after the boxes, so a link is judged on the boxes as the reader sees them.
    /// </summary>
    private async Task FinishArtifactAsync(ReportRun run, IReadOnlyDictionary<string, string> textOf)
    {
        var artifact = run.Artifact;
        if (artifact is not { Error: null }) return;
        foreach (var (field, _) in artifact.CitableTexts().ToList()) artifact.SetText(field, textOf[field]);
        artifact.Compact();
        try
        {
            var verifiedFindings = (run.State.Extractions ?? new List<StudyExtraction>())
                .ToDictionary(e => e.ReferenceNumber, e => (IReadOnlyList<string>)ThematicSynthesis.VerifiedFindings(e).Select(f => f.Finding.Quote).ToList());
            List<ArtifactLinkCheck> decisions;
            using (LlmStage.Begin("citation-check"))
                decisions = await ArtifactLinks.CheckAsync(run.Chat, artifact, run.Papers, verifiedFindings, Llm.ParallelismFor("citation-check"));
            await ArtifactLinks.WriteAsync(GetWorkspaceFolderPath(run.SessionId), decisions);
        }
        catch (Exception ex)
        {
            // A link that could not be checked is not a finding: every link is drawn as the review's reading.
            foreach (var edge in artifact.Edges) edge.Check = ArtifactLinks.Reading;
            _log.LogWarning("Run {RunId}: the artifact's links could not be checked: {Message}", run.SessionId, SanitizeLogMessage(ex.Message));
        }
        artifact.Mermaid = ArtifactBuilder.ToMermaid(artifact);
    }
}
