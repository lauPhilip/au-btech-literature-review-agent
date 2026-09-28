using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuBtechReviewAgent;

/// <summary>
/// Access to .NET logging for classes that are not created by dependency injection (source adapters,
/// static helpers). Program.cs sets the factory at startup; tests and tools get a no-op logger.
/// Replaces Console.WriteLine, whose output is lost on IIS and cannot be filtered by level.
/// </summary>
public static class AppLog
{
    public static ILoggerFactory Factory { get; set; } = NullLoggerFactory.Instance;
    public static ILogger For<T>() => Factory.CreateLogger<T>();
    public static ILogger For(string category) => Factory.CreateLogger(category);
}
