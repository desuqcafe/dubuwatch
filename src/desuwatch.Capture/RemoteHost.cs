using System.Net;
using System.Net.Sockets;

namespace Desuwatch.Capture;

/// <summary>
/// Pure helpers for classifying and grouping remote endpoints.
/// No state, no I/O — safe to call from any thread.
/// </summary>
public static class RemoteHost
{
    public const string LocalNetworkLabel = "Local network";

    /// <summary>
    /// Derives the display grouping key for a remote address.
    ///
    /// If <paramref name="hostname"/> is non-null and non-empty, returns its
    /// registrable (second-level) domain — e.g. "r3.sn-xfge.googlevideo.com"
    /// collapses to "googlevideo.com".
    ///
    /// If the IP is private/link-local/loopback, returns "Local network".
    ///
    /// Otherwise returns the raw IP string so the row is still visible
    /// while DNS resolution is pending.
    /// </summary>
    public static string GetGroupingKey(string ipAddress, string? hostname)
    {
        if (!string.IsNullOrEmpty(hostname))
            return ExtractRegistrableDomain(hostname);

        if (IsPrivateOrLocal(ipAddress))
            return LocalNetworkLabel;

        return ipAddress;
    }

    /// <summary>
    /// Collapses a fully-qualified domain name to its registrable domain.
    /// This is intentionally a simple heuristic — it returns the last two
    /// labels for most TLDs, and the last three for well-known two-part
    /// public suffixes (co.uk, com.au, etc.).
    ///
    /// Not a full Public Suffix List implementation; good enough to turn
    /// "r3---sn-8xgp1vo-xfge.googlevideo.com" into "googlevideo.com".
    /// </summary>
    public static string ExtractRegistrableDomain(string hostname)
    {
        if (string.IsNullOrEmpty(hostname))
            return hostname;

        var host = hostname.TrimEnd('.').ToLowerInvariant();
        var parts = host.Split('.');

        if (parts.Length <= 2)
            return host;

        // Check for common two-part public suffixes.
        var lastTwo = parts[^2] + "." + parts[^1];
        if (IsTwoPartPublicSuffix(lastTwo) && parts.Length >= 3)
            return parts[^3] + "." + lastTwo;

        return parts[^2] + "." + parts[^1];
    }

    private static bool IsTwoPartPublicSuffix(string suffix) => suffix switch
    {
        "co.uk" or "co.jp" or "co.kr" or "co.nz" or "co.za" or "co.in" => true,
        "com.au" or "com.br" or "com.cn" or "com.mx" or "com.tw" or "com.hk" => true,
        "ac.uk" or "gov.uk" or "org.uk" or "net.au" or "org.au" => true,
        _ => false
    };

    /// <summary>
    /// Returns true if the address is in a range that should not be reverse-
    /// looked-up over the public internet: RFC1918 private, loopback,
    /// link-local, IPv6 unique-local, or IPv6 link-local.
    /// </summary>
    public static bool IsPrivateOrLocal(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 link-local
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 0.0.0.0 and 255.255.255.255 and multicast
            if (bytes[0] == 0) return true;
            if (bytes[0] >= 224) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return true;
            // Unique local addresses fc00::/7
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }
}