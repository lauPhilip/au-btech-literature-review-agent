namespace AuBtechReviewAgent;

public interface IAcademicSource
{
    string SourceName { get; }
    Task<List<AcademicPaper>> FetchPapersAsync(string query, int maxResults);
}