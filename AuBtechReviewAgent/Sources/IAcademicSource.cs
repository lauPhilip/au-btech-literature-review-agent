namespace AuBtechReviewAgent;

public interface IAcademicSource
{
    string SourceName { get; }
    Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults = 5);

    /// <summary>
    /// The raw response bodies received by the most recent <see cref="FetchPapersAsync"/> call, exactly as the
    /// source sent them. They are saved with the run (SourceResponses/) because search results change over
    /// time: this is the only way to show later what a search actually returned on the day.
    /// </summary>
    IReadOnlyList<string> LastRawResponses => Array.Empty<string>();
}

/// <summary>Papers linked to one paper by citations, plus the raw responses they came from.</summary>
public record CitationNeighbours(List<AcademicPaper> Backward, List<AcademicPaper> Forward, IReadOnlyList<string> RawResponses);

/// <summary>A citation index that can list the references of a paper and the papers citing it (OpenAlex).</summary>
public interface ICitationGraph
{
    string SourceName { get; }
    Task<CitationNeighbours> GetCitationNeighboursAsync(string doi, int perDirection);
}
