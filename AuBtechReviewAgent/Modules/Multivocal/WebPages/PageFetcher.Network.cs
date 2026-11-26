using System.Net;
using System.Net.Http;

namespace AuBtechReviewAgent;

// Connecting to public addresses only, with the guard the PDF download shares (Web/PublicNetwork.cs).
public sealed partial class PageFetcher
{
    private static SocketsHttpHandler PublicOnlyHandler() => PublicNetwork.Handler();

    /// <summary>Whether an address is on the public internet (<see cref="PublicNetwork.IsPublic"/>).</summary>
    public static bool IsPublic(IPAddress address) => PublicNetwork.IsPublic(address);
}
