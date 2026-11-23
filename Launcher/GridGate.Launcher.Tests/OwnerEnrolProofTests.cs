using System.Security.Cryptography;
using System.Text;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The one-time link and the short proof that let a Region Server or Connected Server owner turn on two-factor.</summary>
public sealed class OwnerEnrolProofTests : IDisposable
{
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0000000-0000-0000-0000-000000000002");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-2fa-link-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (DirectoryNotFoundException) { } catch (IOException) { }
    }

    private string LinkFile => Path.Combine(_dir, "security", "two-factor-link.json");
    private OwnerEnrolLinks Links() => new(LinkFile, _time);
    private OwnerEnrolProof Proof() => new(_dp, _time);

    private static LauncherSettings On(OwnerPlan plan) => new()
    {
        Db = new DatabaseSecrets("", "", ""), Console = new ConsoleSecrets("", ""), MachineId = "", Plan = plan,
    };

    [Theory]
    [InlineData(OwnerPlan.Local, StaffRole.Owner, false)]
    [InlineData(OwnerPlan.SimplePublic, StaffRole.Owner, false)]
    [InlineData(OwnerPlan.GridAsset, StaffRole.Owner, false)]
    [InlineData(OwnerPlan.RegionServer, StaffRole.Owner, true)]
    [InlineData(OwnerPlan.Connected, StaffRole.Owner, true)]
    [InlineData(OwnerPlan.RegionServer, StaffRole.Admin, false)]
    [InlineData(OwnerPlan.Connected, StaffRole.Supervisor, false)]
    public void Applies_only_to_the_owner_on_region_and_connected_plans(OwnerPlan plan, StaffRole role, bool applies) =>
        Assert.Equal(applies, OwnerEnrolRule.Applies(On(plan), role));

    [Fact]
    public void Applies_to_nobody_before_a_plan_is_chosen() =>
        Assert.False(OwnerEnrolRule.Applies(On(OwnerPlan.RegionServer) with { Plan = null }, StaffRole.Owner));

    [Fact]
    public void Link_file_holds_a_hash_not_the_token()
    {
        var token = Links().Create(Ann);

        var text = File.ReadAllText(LinkFile);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), text, StringComparison.Ordinal);
        Assert.True(token.Length >= 40);
    }

    [Fact]
    public void Right_link_is_spent_once()
    {
        var links = Links();
        var token = links.Create(Ann);

        Assert.True(links.Spend(Ann, token));
        Assert.False(File.Exists(LinkFile));
        Assert.False(links.Spend(Ann, token));
    }

    [Fact]
    public void Wrong_link_is_refused_and_kept()
    {
        var links = Links();
        var token = links.Create(Ann);

        Assert.False(links.Spend(Ann, token[..^1] + (token[^1] == 'A' ? 'B' : 'A')));
        Assert.False(links.Spend(Ann, null));
        Assert.False(links.Spend(Ann, ""));
        Assert.True(File.Exists(LinkFile));
        Assert.True(links.Spend(Ann, token));
    }

    [Fact]
    public void Late_link_is_refused_and_kept()
    {
        var links = Links();
        var token = links.Create(Ann);
        _time.Advance(OwnerEnrolLinks.Lasts + TimeSpan.FromSeconds(1));

        Assert.False(links.Spend(Ann, token));
        Assert.True(File.Exists(LinkFile));
    }

    [Fact]
    public void Link_for_another_person_is_refused_and_kept()
    {
        var links = Links();
        var token = links.Create(Ann);

        Assert.False(links.Spend(Bob, token));
        Assert.True(File.Exists(LinkFile));
    }

    [Fact]
    public void Link_lasts_fifteen_minutes() => Assert.Equal(TimeSpan.FromMinutes(15), OwnerEnrolLinks.Lasts);

    [Fact]
    public void New_link_replaces_the_old_one()
    {
        var links = Links();
        var first = links.Create(Ann);
        var second = links.Create(Ann);

        Assert.False(links.Spend(Ann, first));
        Assert.True(links.Spend(Ann, second));
    }

    [Fact]
    public void Damaged_link_file_is_refused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LinkFile)!);
        File.WriteAllText(LinkFile, "{ not json");

        Assert.False(Links().Spend(Ann, "anything"));
    }

    [Fact]
    public void No_link_file_is_refused() => Assert.False(Links().Spend(Ann, "anything"));

    private static HttpRequest RequestWith(HttpResponse response)
    {
        var request = new DefaultHttpContext().Request;
        var cookie = response.Headers.SetCookie.ToString();
        var pair = cookie.Split(';')[0];
        request.Headers.Cookie = pair;
        return request;
    }

    [Fact]
    public void Proof_is_good_for_its_principal_until_it_runs_out()
    {
        var proof = Proof();
        var response = new DefaultHttpContext().Response;
        proof.Issue(response, Ann, OwnerEnrolProof.FromLink);

        var cookie = response.Headers.SetCookie.ToString();
        Assert.StartsWith(OwnerEnrolProof.CookieName + "=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        var request = RequestWith(response);
        Assert.True(proof.Has(request, Ann));
        Assert.False(proof.Has(request, Bob));

        _time.Advance(OwnerEnrolProof.FromLink + TimeSpan.FromSeconds(1));
        Assert.False(proof.Has(request, Ann));
    }

    [Fact]
    public void Proof_lengths_are_one_hour_from_setup_and_thirty_minutes_from_a_link()
    {
        Assert.Equal(TimeSpan.FromHours(1), OwnerEnrolProof.FromSetup);
        Assert.Equal(TimeSpan.FromMinutes(30), OwnerEnrolProof.FromLink);
    }

    [Fact]
    public void Forged_or_other_purpose_cookie_is_refused()
    {
        var proof = Proof();
        var request = new DefaultHttpContext().Request;
        request.Headers.Cookie = OwnerEnrolProof.CookieName + "=abc";
        Assert.False(proof.Has(request, Ann));

        var other = Convert.ToBase64String(_dp.CreateProtector("Something.Else")
            .Protect(Encoding.UTF8.GetBytes($"{Ann:N}|{_time.GetUtcNow().AddHours(1).ToUnixTimeSeconds()}")));
        request.Headers.Cookie = OwnerEnrolProof.CookieName + "=" + Uri.EscapeDataString(other);
        Assert.False(proof.Has(request, Ann));
    }

    [Fact]
    public void No_cookie_is_no_proof() => Assert.False(Proof().Has(new DefaultHttpContext().Request, Ann));

    [Fact]
    public void Clear_deletes_the_cookie()
    {
        var response = new DefaultHttpContext().Response;
        OwnerEnrolProof.Clear(response);

        var cookie = response.Headers.SetCookie.ToString();
        Assert.StartsWith(OwnerEnrolProof.CookieName + "=;", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }
}
