using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Logs;

namespace GridGate.Launcher.Front;

/// <summary>
/// Removes front page pictures that no saved front page setting has mentioned for <see cref="KeepUnused"/>, the same timing as
/// <see cref="Splash.SplashPictureCleanup"/> but over the front store only, so the two never touch each other's pictures (spec
/// 2026-11-19-front-page-design, Constraints). An own list that isn't showing still counts as in use. Each removal is audited
/// as done by GridGate.
/// </summary>
public sealed class FrontPictureCleanup([FromKeyedServices("front")] PictureStore pictures, FrontPageStore store, AuditLog audit,
    TimeProvider time, ILogger<FrontPictureCleanup> log) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);
    public static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan KeepUnused = TimeSpan.FromDays(30);

    private readonly SemaphoreSlim _one = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRun, time, stoppingToken);
            using var timer = new PeriodicTimer(Period, time);
            do
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Unused front page pictures could not be cleaned up: {Message}", e.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass (the loop's work; tests call it directly).</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var kept = FrontPictures.Kept(store.Load());
            pictures.MarkUse(kept);
            var now = time.GetUtcNow();
            foreach (var p in pictures.List())
            {
                if (p.UnusedSince is not { } since || now - since < KeepUnused || kept.Contains(p.Name)) continue;
                // Checked again under the store's lock: a save since this pass began may use it now.
                if (store.RemovePictureIfUnused(p.Name, pictures.Delete))
                    audit.Write(new AuditEntry("GridGate", null, "system", null, "front-image-deleted", $"{p.Name} ({p.Label})", "unused for 30 days"));
            }
        }
        finally
        {
            _one.Release();
        }
    }
}
