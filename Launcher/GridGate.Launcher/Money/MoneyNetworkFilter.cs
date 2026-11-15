using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace GridGate.Launcher.Money;

/// <summary>
/// Who may connect to the money port on a Grid Asset Server, which listens on every address so its Region Servers can
/// reach it (money design §7.2): this computer and its own network only. Anything else is dropped before TLS, even if a
/// router forwards the port by mistake.
/// </summary>
public static class MoneyNetworkFilter
{
    /// <summary>
    /// Loopback; IPv4 private (10/8, 172.16/12, 192.168/16) or link-local (169.254/16); IPv6 unique-local (fc00::/7) or
    /// link-local (fe80::/10). IPv4 mapped into IPv6 is judged as IPv4. Shared ISP space (100.64/10) is other customers,
    /// not this network, so it is refused.
    /// </summary>
    public static bool Allowed(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var v6 = ip.GetAddressBytes();
        return ip.IsIPv6LinkLocal || (v6[0] & 0xFE) == 0xFC;
    }

    /// <summary>A connection middleware that closes a connection from an address <see cref="Allowed"/> refuses, and counts it.</summary>
    public static Func<ConnectionDelegate, ConnectionDelegate> Middleware(MoneyAuthFailures failures, ILogger log) => next => async connection =>
    {
        if (connection.RemoteEndPoint is IPEndPoint remote && !Allowed(remote.Address))
        {
            failures.RecordFailure(remote.Address);
            if (failures.FirstDropInWindow(remote.Address))
                log.LogWarning("Refused money connections from {Address}: it isn't on this computer's network. (Once per {Minutes} minutes.)",
                    remote.Address, (int)MoneyAuthFailures.Window.TotalMinutes);
            connection.Abort();
            return;
        }
        await next(connection);
    };
}
