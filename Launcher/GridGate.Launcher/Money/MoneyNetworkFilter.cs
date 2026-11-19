using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace GridGate.Launcher.Money;

/// <summary>One network this computer is on: an address and how many leading bits of it name the network.</summary>
public sealed record OwnSubnet(IPAddress Address, int PrefixLength)
{
    /// <summary>Whether <paramref name="ip"/> is on this network. Addresses of the other family never are.</summary>
    public bool Contains(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != Address.AddressFamily) return false;
        var a = Address.GetAddressBytes();
        var b = ip.GetAddressBytes();
        if (PrefixLength is < 1 || PrefixLength > a.Length * 8) return false;
        var whole = PrefixLength / 8;
        for (var i = 0; i < whole; i++)
            if (a[i] != b[i]) return false;
        var rest = PrefixLength % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[whole] & mask) == (b[whole] & mask);
    }
}

/// <summary>
/// Who may connect to the money port on a Grid Asset Server, which listens on every address so its Region Servers can
/// reach it (money design §7.2): this computer and the networks it is on. Anything else is dropped before TLS, even if a
/// router forwards the port by mistake.
/// </summary>
public static class MoneyNetworkFilter
{
    /// <summary>How long this computer's own networks are kept before they are read again (an address may change).</summary>
    public static readonly TimeSpan SubnetsLifetime = TimeSpan.FromSeconds(30);

    /// <summary>At most one refused-caller line per address in this long.</summary>
    public static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(1);

    private const int MaxLogged = 4096;

    private static readonly Lock CacheGate = new();
    private static IReadOnlyList<OwnSubnet> _cached = [];
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>The networks this computer's own interfaces are on now, read at most every <see cref="SubnetsLifetime"/>.</summary>
    public static IReadOnlyList<OwnSubnet> OwnSubnets()
    {
        lock (CacheGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _cachedAt < SubnetsLifetime) return _cached;
            _cached = ReadInterfaces();
            _cachedAt = now;
            return _cached;
        }
    }

    private static List<OwnSubnet> ReadInterfaces()
    {
        var found = new List<OwnSubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && u.PrefixLength > 0)
                        found.Add(new OwnSubnet(u.Address, u.PrefixLength));
            }
        }
        catch (NetworkInformationException)
        {
            // Nothing readable: only loopback passes, which fails closed.
        }
        return found;
    }

    /// <summary>
    /// Loopback, or a private address (IPv4 10/8, 172.16/12, 192.168/16 or link-local 169.254/16; IPv6 unique-local fc00::/7 or
    /// link-local fe80::/10) that is on one of <paramref name="own"/>, the networks of this computer's own interfaces. A
    /// private address on some other network (a router's other side, a neighbour's) is refused. IPv4 mapped into IPv6 is
    /// judged as IPv4. Shared ISP space (100.64/10) is other customers, not this network, so it is refused.
    /// </summary>
    public static bool Allowed(IPAddress ip, IReadOnlyList<OwnSubnet> own)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        return IsPrivate(ip) && own.Any(s => s.Contains(ip));
    }

    /// <summary><see cref="Allowed(IPAddress, IReadOnlyList{OwnSubnet})"/> against this computer's own networks now.</summary>
    public static bool Allowed(IPAddress ip) => Allowed(ip, OwnSubnets());

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var v6 = ip.GetAddressBytes();
        return ip.IsIPv6LinkLocal || (v6[0] & 0xFE) == 0xFC;
    }

    /// <summary>
    /// A connection middleware that closes a connection from an address <see cref="Allowed(IPAddress, IReadOnlyList{OwnSubnet})"/>
    /// refuses, and counts it. Each refused address is logged as a warning at most once a minute. <paramref name="ownSubnets"/>
    /// and <paramref name="time"/> are for tests; by default this computer's own networks and the system clock.
    /// </summary>
    public static Func<ConnectionDelegate, ConnectionDelegate> Middleware(MoneyAuthFailures failures, ILogger log,
        Func<IReadOnlyList<OwnSubnet>>? ownSubnets = null, TimeProvider? time = null)
    {
        var networks = ownSubnets ?? OwnSubnets;
        var clock = time ?? TimeProvider.System;
        var lastLogged = new ConcurrentDictionary<IPAddress, DateTimeOffset>();
        return next => async connection =>
        {
            if (connection.RemoteEndPoint is IPEndPoint remote && !Allowed(remote.Address, networks()))
            {
                failures.RecordFailure(remote.Address);
                if (ShouldLog(lastLogged, remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address, clock.GetUtcNow()))
                    log.LogWarning("Refused a money connection from {Address}: it isn't on this computer's own network. (At most one line a minute per address.)",
                        remote.Address);
                connection.Abort();
                return;
            }
            await next(connection);
        };
    }

    private static bool ShouldLog(ConcurrentDictionary<IPAddress, DateTimeOffset> lastLogged, IPAddress address, DateTimeOffset now)
    {
        if (lastLogged.Count >= MaxLogged)
            foreach (var (old, at) in lastLogged)
                if (now - at >= LogEvery) lastLogged.TryRemove(old, out _);
        // Still full of recent addresses: say nothing more rather than grow without limit.
        if (lastLogged.Count >= MaxLogged && !lastLogged.ContainsKey(address)) return false;
        var log = false;
        lastLogged.AddOrUpdate(address, _ =>
        {
            log = true;
            return now;
        }, (_, at) =>
        {
            if (now - at < LogEvery) return at;
            log = true;
            return now;
        });
        return log;
    }
}
