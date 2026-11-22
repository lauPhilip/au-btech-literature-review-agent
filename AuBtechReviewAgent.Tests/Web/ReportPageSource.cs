namespace AuBtechReviewAgent.Tests;

/// <summary>
/// The report page's source as one text, for tests that read it: SpecMatrix.razor with each section component
/// (&lt;SpecMatrixX Page="this" /&gt;) put back where its tag stands, followed by the page's code-behind files. The sections
/// read the page's state through "Page." (and its static helpers through "SpecMatrix."), which is taken out, so a test
/// sees the markup as the page renders it.
/// </summary>
internal static class ReportPageSource
{
    public static string? Read() => PageSource.Read("SpecMatrix");
}
