using GridGate.Launcher.Core.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Pairing codes and proofs (money plan 3, task 5).</summary>
public sealed class MoneyPairingTests
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly string F1 = new('A', 64), F2 = new('B', 64);
    private const string Nonce = "bm9uY2Vub25jZW5vbmNlbm9uY2Vub25jZW5vbmNlMTI=";

    [Fact]
    public void Code_has_80_bits_in_crockford()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => PairingCode.New()).ToList();

        Assert.All(codes, c => Assert.Equal(16, c.Length));
        Assert.All(codes, c => Assert.All(c, ch => Assert.Contains(ch, Alphabet)));
        Assert.Equal(1000, codes.Distinct().Count());
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", PairingCode.Show(codes[0]));
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs", "ABCDEFGHJKMNPQRS")]
    [InlineData(" ABCD EFGH JKMN PQRS ", "ABCDEFGHJKMNPQRS")]
    [InlineData("O0OO-IiLl-0000-1111", "00001111" + "00001111")]
    [InlineData("ABCDEFGHJKMNPQR", null)]        // 15 characters
    [InlineData("ABCDEFGHJKMNPQRSU", null)]      // 17
    [InlineData("ABCDEFGHJKMNPQRU", null)]       // U isn't in the alphabet
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Typed_codes_normalize(string? typed, string? expected) => Assert.Equal(expected, PairingCode.Normalize(typed));

    [Fact]
    public void Proof_is_bound_to_the_fingerprint_nonce_and_host()
    {
        var code = PairingCode.New();
        var p = PairingProof.Client(code, F1, Nonce, "192.168.1.20");

        Assert.Matches("^[0-9A-F]{64}$", p);
        Assert.NotEqual(p, PairingProof.Client(code, F2, Nonce, "192.168.1.20"));
        Assert.NotEqual(p, PairingProof.Client(code, F1, Nonce, "192.168.1.30"));
        Assert.NotEqual(p, PairingProof.Client(PairingCode.New(), F1, Nonce, "192.168.1.20"));
        Assert.NotEqual(p, PairingProof.Server(code, F1, Nonce, "192.168.1.20"));
        Assert.True(PairingProof.Same(p, p.ToLowerInvariant()));
        Assert.False(PairingProof.Same(p, "zz"));
    }

    [Fact]
    public void Redeem_works_once()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var (code, _) = pairing.Issue();

        Assert.Equal(code, pairing.Redeem(c => c == code));
        Assert.Null(pairing.Redeem(c => c == code));
        Assert.Null(pairing.PendingUntil);
    }

    [Fact]
    public void Five_wrong_proofs_from_one_address_block_that_address_but_not_the_code()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var (code, _) = pairing.Issue();
        var noisy = System.Net.IPAddress.Parse("192.168.1.50");

        for (var i = 0; i < MoneyPairing.MaxWrongProofs; i++) Assert.Null(pairing.Redeem(_ => false, noisy));

        // Even the right proof from that address is refused now, and isn't even tried.
        var tried = false;
        Assert.Null(pairing.Redeem(c => tried = c == code, noisy));
        Assert.False(tried);
        Assert.NotNull(pairing.PendingUntil);
        // The code is still good for another address.
        Assert.Equal(code, pairing.Redeem(c => c == code, System.Net.IPAddress.Parse("192.168.1.51")));
    }

    [Fact]
    public void Fifty_wrong_proofs_in_all_cancel_the_code()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var (code, _) = pairing.Issue();

        // Four from each of many addresses: none is blocked, but the total adds up.
        for (var i = 0; i < MoneyPairing.MaxWrongProofsInAll; i++)
            Assert.Null(pairing.Redeem(_ => false, new System.Net.IPAddress([10, 0, (byte)(i / 4), (byte)(i / 4)])));

        Assert.Null(pairing.Redeem(c => c == code, System.Net.IPAddress.Parse("10.9.9.9")));
        Assert.Null(pairing.PendingUntil);
    }

    [Fact]
    public void Forty_nine_wrong_proofs_in_all_leave_the_code_usable()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var (code, _) = pairing.Issue();

        for (var i = 0; i < MoneyPairing.MaxWrongProofsInAll - 1; i++)
            pairing.Redeem(_ => false, new System.Net.IPAddress([10, 0, (byte)(i / 4), 1]));

        Assert.Equal(code, pairing.Redeem(c => c == code, System.Net.IPAddress.Parse("10.9.9.9")));
    }

    [Fact]
    public void A_new_code_starts_every_address_afresh()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        pairing.Issue();
        var noisy = System.Net.IPAddress.Parse("192.168.1.50");
        for (var i = 0; i < MoneyPairing.MaxWrongProofs; i++) pairing.Redeem(_ => false, noisy);

        var (second, _) = pairing.Issue();

        Assert.Equal(second, pairing.Redeem(c => c == second, noisy));
    }

    [Fact]
    public void Four_wrong_proofs_leave_it_usable()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var (code, _) = pairing.Issue();

        for (var i = 0; i < MoneyPairing.MaxWrongProofs - 1; i++) pairing.Redeem(_ => false);

        Assert.Equal(code, pairing.Redeem(c => c == code));
    }

    [Fact]
    public void Expired_code_is_refused()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var pairing = new MoneyPairing(time);
        var (code, until) = pairing.Issue();
        Assert.Equal(time.GetUtcNow() + MoneyPairing.Lifetime, until);

        time.Advance(MoneyPairing.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Null(pairing.Redeem(c => c == code));
        Assert.Null(pairing.PendingUntil);
    }

    [Fact]
    public void New_code_replaces_the_old()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var (first, _) = pairing.Issue();
        var (second, _) = pairing.Issue();

        Assert.Null(pairing.Redeem(c => c == first));
        Assert.Equal(second, pairing.Redeem(c => c == second));
    }

    [Fact]
    public void Cancel_drops_the_code()
    {
        var pairing = new MoneyPairing(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var (code, _) = pairing.Issue();

        pairing.Cancel();

        Assert.Null(pairing.Redeem(c => c == code));
    }

    [Fact]
    public void Paired_server_grants_carry_their_host()
    {
        var key = "server-key-0123456789012345678901234567890123456";
        var s = TestSettings.Create() with
        {
            Money = new MoneySettings(PairedServers: [new PairedServer(Guid.NewGuid(), "192.168.1.20", PairedServer.HashOf(key), DateTimeOffset.UtcNow)]),
        };

        Assert.True(RegionKeyRing.FromSettings(s).TryAuthenticate(key, out var grant));
        Assert.Equal("192.168.1.20", grant!.ServerHost);
        Assert.StartsWith("server:", grant.KeyId);
        Assert.Empty(grant.Regions);
        Assert.False(RegionKeyRing.FromSettings(s).TryAuthenticate(key + "x", out _));
    }
}
