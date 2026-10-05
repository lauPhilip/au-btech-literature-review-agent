namespace AuBtechReviewAgent.Tests;

/// <summary>Clean-up of the temporary folders the tests write to.</summary>
internal static class TestFolders
{
    /// <summary>
    /// Deletes a temporary test folder. A folder the OS still holds open is left for the system's temp
    /// clean-up rather than failing the test run; returns whether the folder is gone.
    /// </summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
