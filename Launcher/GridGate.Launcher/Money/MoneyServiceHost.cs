using System.Net;
using System.Security.Cryptography.X509Certificates;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Money;

/// <summary>
/// The region-facing money service (money design §7.2): its own Kestrel host inside the launcher, on its own HTTPS port,
/// with nothing but the money endpoints. It listens on 127.0.0.1 only, except on a Grid Asset Server, where it listens on
/// every address for its Region Servers and drops connections from outside this computer's network (money plan 3). It
/// doesn't start while the mode it is given is Off.
/// It has no logging providers of its own, so Kestrel writes no request lines; the endpoints log through the launcher's.
/// </summary>
public sealed class MoneyServiceHost(
    MoneyLedger ledger,
    PresenceCheck presence,
    Func<LauncherSettings> settings,
    Func<MoneyMode> effective,
    Func<X509Certificate2> certificate,
    MoneyAuthFailures failures,
    ILoggerFactory loggers,
    MoneyStatus? status = null,
    MoneyPairing? pairing = null,
    Func<Func<LauncherSettings, LauncherSettings>, LauncherSettings>? update = null,
    RegionHosts? hosts = null) : IHostedService
{
    /// <summary>A request body larger than this is refused before it is read.</summary>
    public const long MaxBodyBytes = 16 * 1024;

    private readonly ILogger _log = loggers.CreateLogger<MoneyServiceHost>();
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (effective() == MoneyMode.Off) return;
        var port = settings().MoneyPort;
        var lan = settings().Plan == OwnerPlan.GridAsset;
        var where = lan ? $"every address (this network only), port {port}" : $"127.0.0.1:{port}";
        WebApplication? app = null;
        try
        {
            var cert = certificate();
            app = Build(port, cert, lan);
            await app.StartAsync(cancellationToken);
            _app = app;
            if (status is not null) status.ServiceError = null;
            _log.LogInformation("The money service is listening on {Where}.", where);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Money being down must not take the launcher or the grid with it (money design §5): payments are refused
            // until the next start.
            _log.LogError("The money service couldn't start on {Where}: {Error}: {Message}", where, e.GetType().Name, e.Message);
            // The dashboard shows the type only: a message can carry paths or addresses.
            if (status is not null) status.ServiceError = e.GetType().Name;
            if (app is not null) await app.DisposeAsync();
        }
    }

    private WebApplication Build(int port, X509Certificate2 cert, bool lan)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = MaxBodyBytes;
            if (lan)
                o.ListenAnyIP(port, l =>
                {
                    // Before TLS: an address outside this network never gets a handshake.
                    l.Use(MoneyNetworkFilter.Middleware(failures, _log));
                    l.UseHttps(cert);
                });
            else
                o.Listen(IPAddress.Loopback, port, l => l.UseHttps(cert));
        });
        builder.Services.AddRoutingCore();
        var app = builder.Build();
        app.UseRouting();
        MoneyApi.Map(app, new MoneyApi.Context(ledger, presence, settings, failures, _log, pairing, update, hosts, MoneyCertificate.Fingerprint(cert)));
        return app;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Taken once: the host may stop it from two places at once (seen when a test host is disposed).
        if (Interlocked.Exchange(ref _app, null) is not { } app) return;
        await app.StopAsync(cancellationToken);
        await app.DisposeAsync();
    }
}
