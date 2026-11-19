using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Money;

/// <summary>What a Region Server's pairing looks like from its side, as its Money page shows it.</summary>
public enum PairingHealth { Ok, Refused, CertificateChanged, Unreachable, NotPaired, NoRegions, SomeRegions }

/// <summary>
/// A pairing attempt: the pairing to save, or an owner-readable reason nothing was saved; and the grid information it read
/// fresh, which the page saves with the pairing (the saved join is only refreshed when the grid service starts).
/// </summary>
public sealed record PairOutcome(ServerPairing? Pairing, string? Error, JoinedGrid? Grid = null);

/// <summary>A Region Server's side of pairing (money plan 3); a seam so the page can be tested.</summary>
public interface IMoneyPairClient
{
    Task<PairOutcome> PairAsync(LauncherSettings s, string typedCode, CancellationToken ct);
    Task<PairingHealth> CheckAsync(LauncherSettings s, CancellationToken ct);
}

/// <summary>
/// Pairs this Region Server with its Grid Asset Server's money service (money design §7.7, money plan 3). It reads the
/// grid info fresh, then talks to the service over HTTPS pinned to the published fingerprint, sends a proof of the code
/// (never the code), and saves the key only if the service proves back that it made the code. Nothing secret is logged.
/// </summary>
public sealed class MoneyPairClient(IGridJoinClient join, TimeProvider time) : IMoneyPairClient
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PairAnswer(string? Code, string? Key, string? Proof)
    {
        // What this record holds is never printed, so a log line or an exception that shows it can't leak it.
        private bool PrintMembers(System.Text.StringBuilder sb)
        {
            sb.Append($"Code = hidden, Key = hidden, Proof = hidden");
            return true;
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder("PairAnswer { ");
            PrintMembers(sb);
            return sb.Append(" }").ToString();
        }
    }
    private sealed record HealthAnswer(string? Code, bool? Ok, int? Regions);

    public async Task<PairOutcome> PairAsync(LauncherSettings s, string typedCode, CancellationToken ct)
    {
        if (PairingCode.Normalize(typedCode) is not { } code)
            return new(null, "That isn't a pairing code. It has 16 letters and numbers, like 7K2M-9QXD-4HTB-ZW3R.");
        if (s.Plan != OwnerPlan.RegionServer || s.Join is not { } joined)
            return new(null, "Only a Region Server pairs with its grid's money.");

        JoinedGrid grid;
        try
        {
            grid = await join.ReadAsync(joined.Host, joined.PublicPort, ct);
        }
        catch (GridJoinException e)
        {
            return new(null, e.Message);
        }
        switch (grid.Money)
        {
            case null or MoneyMode.Off:
                return new(null, "The grid's money is off, so there's nothing to pair.");
            case MoneyMode.Gloebit:
                return new(null, "The grid uses Gloebit. Gloebit is set up on each computer; there's nothing to pair.");
        }
        if (grid.MoneyPort is not { } port || grid.MoneyFingerprint is not { } fingerprint)
            return new(null, "The Grid Asset Server's GridGate is too old to pair. Update it first.");

        var host = HostAddress.Effective(s.Hostname);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        PairAnswer? answer;
        try
        {
            using var http = Pinned(fingerprint, out _);
            using var response = await http.PostAsJsonAsync($"https://{joined.Host}:{port}/money/v1/pair",
                new { host, firstPort = FirstPort(s), machineId = s.MachineId, nonce, proof = PairingProof.Client(code, fingerprint, nonce, host) }, Json, ct);
            answer = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<PairAnswer>(Json, ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            return new(null, $"Couldn't reach the grid's money service at {joined.Host}:{port}.");
        }

        if (answer is { Code: "ok", Key: { Length: > 0 } key, Proof: { } proof })
            return PairingProof.Same(PairingProof.Server(code, fingerprint, nonce, key), proof)
                ? new(new ServerPairing(joined.Host, fingerprint, key, time.GetUtcNow()), null, grid)
                : new(null, "The Grid Asset Server didn't prove it made this code. Nothing was saved.");
        return new(null, "The Grid Asset Server didn't accept this code. Codes work once, for 15 minutes; make a new one there.");
    }

    /// <summary>
    /// The lowest region port this server uses: its first region port, or a live region's port below it (Advanced → Network
    /// can raise the first port without moving existing regions). The key covers from here up (second review, Important).
    /// </summary>
    public static int FirstPort(LauncherSettings s) =>
        s.Regions.Where(r => r.DeletedAt is null && r.UdpPort > 0).Select(r => r.UdpPort).Append(s.RegionPort).Min();

    /// <summary>This server's regions the grid should see: those not deleted or parked.</summary>
    private static int LiveRegions(LauncherSettings s) => s.Regions.Count(r => r.DeletedAt is null && !r.Parked);

    public async Task<PairingHealth> CheckAsync(LauncherSettings s, CancellationToken ct)
    {
        if (s.Money.Pairing is not { } pairing || s.Join is not { } joined || !string.Equals(pairing.GridHost, joined.Host, StringComparison.OrdinalIgnoreCase))
            return PairingHealth.NotPaired;
        int port;
        try
        {
            var grid = await join.ReadAsync(joined.Host, joined.PublicPort, ct);
            if (grid.MoneyFingerprint is { } published && !string.Equals(published, pairing.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return PairingHealth.CertificateChanged;
            if (grid.MoneyPort is not { } p) return PairingHealth.Unreachable;
            port = p;
        }
        catch (GridJoinException)
        {
            return PairingHealth.Unreachable;
        }

        var pinFailed = false;
        try
        {
            using var http = Pinned(pairing.Fingerprint, out var failed);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{joined.Host}:{port}/money/v1/health") { Content = JsonContent.Create(new { }) };
            request.Headers.TryAddWithoutValidation("Authorization", "GridGateRegion " + pairing.Key);
            try
            {
                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return PairingHealth.Refused;
                if (response.StatusCode != HttpStatusCode.OK) return PairingHealth.Unreachable;
                // A 200 can still be {"code":"unavailable"} (the grid can't read its region list right now).
                var health = await response.Content.ReadFromJsonAsync<HealthAnswer>(Json, ct);
                return health switch
                {
                    { Ok: true, Regions: 0 } => PairingHealth.NoRegions,
                    { Ok: true, Regions: { } seen } when seen < LiveRegions(s) => PairingHealth.SomeRegions,
                    { Ok: true } => PairingHealth.Ok,
                    _ => PairingHealth.Unreachable,
                };
            }
            finally
            {
                pinFailed = failed();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return pinFailed ? PairingHealth.CertificateChanged : PairingHealth.Unreachable;
        }
    }

    /// <summary>
    /// An HTTP client that trusts only a certificate whose SHA-256 is <paramref name="fingerprint"/>, as the region module's
    /// client does: the pin is the trust, so chain and name errors don't matter. <paramref name="pinFailed"/> says whether a
    /// certificate was refused for not matching.
    /// </summary>
    private static HttpClient Pinned(string fingerprint, out Func<bool> pinFailed)
    {
        var failed = false;
        pinFailed = () => failed;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    var ok = cert is not null && string.Equals(
                        MoneyCertificate.Fingerprint(X509CertificateLoader.LoadCertificate(cert.GetRawCertData())), fingerprint, StringComparison.OrdinalIgnoreCase);
                    if (!ok) failed = true;
                    return ok;
                },
            },
        };
        return new HttpClient(handler) { Timeout = Timeout };
    }
}
