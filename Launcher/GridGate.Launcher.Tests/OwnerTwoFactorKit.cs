using System.Runtime.CompilerServices;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Turns two-factor on for the test owner on a test host and hands out current codes, for the forms on a Region Server that ask
/// the owner for one. The host's clock is moved on 31 seconds for each code, so a code is never one already used.
/// </summary>
internal static class OwnerTwoFactorKit
{
    private static readonly ConditionalWeakTable<LauncherFactory, byte[]> Secrets = new();
    private static readonly ConditionalWeakTable<HttpClient, LauncherFactory> Clients = new();

    /// <summary>Remembers which host a signed-in owner client talks to, so a form post can add a code (see <see cref="CodeFor"/>).</summary>
    public static HttpClient Track(HttpClient client, LauncherFactory f)
    {
        Clients.AddOrUpdate(client, f);
        return client;
    }

    /// <summary>A current code for a tracked owner client with two-factor on, otherwise null.</summary>
    public static string? CodeFor(HttpClient client) =>
        Clients.TryGetValue(client, out var f) && Secrets.TryGetValue(f, out _) ? NextCode(f) : null;
    /// <summary>Two-factor on for <see cref="TestAuth.OwnerId"/>; does nothing the second time.</summary>
    public static void Enrol(LauncherFactory f)
    {
        if (Secrets.TryGetValue(f, out _)) return;
        var store = f.Services.GetRequiredService<TwoFactorStore>();
        var (secret, _) = store.BeginEnrol(TestAuth.OwnerId, "Test Owner", "GridGate");
        Assert.NotNull(store.ConfirmEnrol(TestAuth.OwnerId, Totp.Code(secret, Totp.StepAt(f.Time.GetUtcNow())), secret));
        Secrets.Add(f, secret);
    }

    /// <summary>The owner's current code, one step past anything used; turns two-factor on first if needed.</summary>
    public static string NextCode(LauncherFactory f)
    {
        Enrol(f);
        f.Time.Advance(TimeSpan.FromSeconds(31));
        Secrets.TryGetValue(f, out var secret);
        return Totp.Code(secret!, Totp.StepAt(f.Time.GetUtcNow()));
    }
}
