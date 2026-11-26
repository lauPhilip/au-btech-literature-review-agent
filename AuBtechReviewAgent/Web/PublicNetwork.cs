using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AuBtechReviewAgent;

/// <summary>
/// Connecting only to the public internet, for every address that came from outside: a web page of the multivocal
/// review, an open-access PDF from a database record, Unpaywall or Zenodo. Without it, a record could point the
/// server at itself, its private network or the cloud metadata service (server-side request forgery, #588).
/// </summary>
public static class PublicNetwork
{
    /// <summary>
    /// A handler that connects to public addresses only and never follows a redirect itself, so the caller follows
    /// each one by hand and checks the new address (<see cref="WhyNotAllowed"/>).
    /// </summary>
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancel) =>
        {
            // Checked when connecting, not when reading the link, so a name that resolves to a private address is
            // refused too. The address checked is the address connected to.
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel);
            var target = addresses.FirstOrDefault(IsPublic)
                ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} is not a public address");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>
    /// Why an address from outside is not fetched, or null when it may be: only http and https, no user name or
    /// password, and no literal address that is not public. A name is checked again when it is connected to.
    /// </summary>
    public static string? WhyNotAllowed(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return "not a web address";
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return "not an http or https address";
        if (uri.UserInfo.Length > 0) return "the address carries a user name or password";
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(uri.DnsSafeHost, out var literal) && !IsPublic(literal))
            return "the address is not on the public internet";
        if (string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.DnsSafeHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return "the address is not on the public internet";
        return null;
    }

    /// <summary>IPv4 ranges that are not on the public internet, as network and mask.</summary>
    private static readonly (uint Network, uint Mask)[] NonPublicIPv4 = new[]
    {
        Range(0, 0, 8), // "this" network
        Range(10, 0, 8), // private
        Range(100, 64, 10), // shared address space (carrier NAT)
        Range(127, 0, 8), // loopback
        Range(169, 254, 16), // link-local, including cloud metadata services
        Range(172, 16, 12), // private
        Range(192, 0, 24), // IETF protocol assignments
        Range(192, 168, 16), // private
        Range(198, 18, 15), // benchmarking
        Range(224, 0, 3), // multicast and reserved
    };

    private static (uint Network, uint Mask) Range(byte first, byte second, int prefixLength)
    {
        uint mask = uint.MaxValue << (32 - prefixLength);
        return (((uint)first << 24 | (uint)second << 16) & mask, mask);
    }

    /// <summary>Whether an address is on the public internet: not this machine, a private network, link-local or multicast.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            uint value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            return !NonPublicIPv4.Any(range => (value & range.Mask) == range.Network);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC); // unique local fc00::/7
        }
        return false;
    }
}
