using System;
using System.IO;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>
/// Reads a running review's ledger (transparent-process.json) for the dashboard, which looks at it every second
/// (issue #306). The ledger is parsed again only when its length or write time changed; otherwise the last read is
/// given back as it is. Long steps such as writing and checking the review do not save the ledger for minutes, and a
/// ledger of a few hundred studies takes milliseconds and megabytes to parse.
/// </summary>
public sealed class RunLedgerWatch
{
    private readonly string _path;
    private (long Length, DateTime Written, ReviewState State, string Raw)? _last;

    public RunLedgerWatch(string path) => _path = path;

    /// <summary>How many times the ledger was parsed; for tests.</summary>
    public int Parses { get; private set; }

    /// <summary>
    /// The ledger and its text, or null when it is missing or cannot be read right now (for example while it is
    /// replaced); the caller tries again on its next tick.
    /// </summary>
    public (ReviewState State, string Raw)? Read()
    {
        var info = new FileInfo(_path);
        if (!info.Exists) return null;
        if (_last is { } last && last.Length == info.Length && last.Written == info.LastWriteTimeUtc) return (last.State, last.Raw);

        try
        {
            string raw;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                raw = reader.ReadToEnd();
            Parses++;
            if (JsonSerializer.Deserialize<ReviewState>(raw) is not { } state) return null;
            // The time read before the text: if the file changed in between, the next tick reads it again.
            _last = (info.Length, info.LastWriteTimeUtc, state, raw);
            return (state, raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
