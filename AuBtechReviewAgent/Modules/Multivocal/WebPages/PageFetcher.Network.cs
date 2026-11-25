using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace AuBtechReviewAgent;

// Connecting to public addresses only: the handler checks the address it connects to, so a name that resolves to a
// private address is refused too.
public sealed partial class PageFetcher
{
    internal static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        AllowAutoRedirect = false, // followed by hand, so each hop is checked
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
