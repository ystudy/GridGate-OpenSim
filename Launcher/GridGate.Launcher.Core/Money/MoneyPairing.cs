using System.Security.Cryptography;
using System.Text;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// The one-time code that pairs a Region Server with its Grid Asset Server's money (money design §7.7; money plan 3):
/// 16 characters of Crockford base32, so 80 bits, shown as four groups.
/// </summary>
public static class PairingCode
{
    public const int Length = 16;
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New() => new(RandomNumberGenerator.GetItems<char>(Alphabet, Length));

    public static string Show(string code) => string.Join("-", Enumerable.Range(0, Length / 4).Select(i => code.Substring(i * 4, 4)));

    /// <summary>
    /// The code as typed, made canonical: upper case, without spaces or dashes, with O read as 0 and I or L as 1. Null unless
    /// exactly 16 alphabet characters are left.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null) return null;
        var chars = typed.ToUpperInvariant().Where(c => c is not (' ' or '-')).Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray();
        return chars.Length == Length && chars.All(Alphabet.Contains) ? new string(chars) : null;
    }
}

/// <summary>
/// The pairing proofs (money plan 3). The Region Server proves it has the code without sending it, bound to the
/// certificate fingerprint it actually saw, so a machine in the middle with another certificate can't use the proof. The
/// Grid Asset Server proves the same back, bound to the key it hands out.
/// </summary>
public static class PairingProof
{
    public const string Version = "gridgate-pair-v1";

    public static string Client(string code, string fingerprint, string nonce, string host) =>
        Mac(code, $"{Version}|client|{fingerprint}|{nonce}|{host}");

    public static string Server(string code, string fingerprint, string nonce, string key) =>
        Mac(code, $"{Version}|server|{fingerprint}|{nonce}|{key}");

    /// <summary>Whether two hex proofs are the same, in constant time; anything that isn't 32 bytes of hex is not.</summary>
    public static bool Same(string a, string b)
    {
        if (!TryHex(a, out var x) || !TryHex(b, out var y)) return false;
        return CryptographicOperations.FixedTimeEquals(x, y);
    }

    private static bool TryHex(string s, out byte[] bytes)
    {
        bytes = [];
        if (s.Length != 64) return false;
        try
        {
            bytes = Convert.FromHexString(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Mac(string code, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(message)));
}

/// <summary>
/// The Grid Asset Server's pending pairing code, in memory only: one at a time, for 15 minutes, cancelled after 50 wrong
/// proofs, gone when the launcher restarts (money plan 3).
/// </summary>
public sealed class MoneyPairing(TimeProvider time)
{
    /// <summary>Wrong proofs from one address that block it for the code's lifetime.</summary>
    public const int MaxWrongProofs = 5;

    /// <summary>Wrong proofs from any addresses that cancel the code.</summary>
    public const int MaxWrongProofsInAll = 50;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private string? _code;
    private DateTimeOffset _expires;
    private int _wrong;
    private readonly Dictionary<System.Net.IPAddress, int> _wrongBy = [];

    /// <summary>A new code, replacing any pending one, and when it stops working.</summary>
    public (string Code, DateTimeOffset Expires) Issue()
    {
        lock (_gate)
        {
            _code = PairingCode.New();
            _expires = time.GetUtcNow() + Lifetime;
            _wrong = 0;
            _wrongBy.Clear();
            return (_code, _expires);
        }
    }

    public void Cancel()
    {
        lock (_gate) _code = null;
    }

    /// <summary>When the pending code stops working, or null when there is none.</summary>
    public DateTimeOffset? PendingUntil
    {
        get
        {
            lock (_gate) return Live() ? _expires : null;
        }
    }

    /// <summary>
    /// The pending code, used up, when <paramref name="proofMatches"/> says the proof was made with it; otherwise null. A wrong
    /// proof counts against the address it came from (<paramref name="from"/>): after <see cref="MaxWrongProofs"/> that
    /// address is blocked for this code's lifetime and its proofs aren't even tried, while the code stays good for other
    /// addresses. <see cref="MaxWrongProofsInAll"/> wrong proofs from anywhere cancel the code.
    /// </summary>
    public string? Redeem(Func<string, bool> proofMatches, System.Net.IPAddress? from = null)
    {
        lock (_gate)
        {
            if (!Live()) return null;
            var address = from is null ? System.Net.IPAddress.None : from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from;
            _wrongBy.TryGetValue(address, out var wrongHere);
            if (wrongHere >= MaxWrongProofs) return null;
            var code = _code!;
            if (proofMatches(code))
            {
                _code = null;
                return code;
            }
            _wrongBy[address] = wrongHere + 1;
            if (++_wrong >= MaxWrongProofsInAll) _code = null;
            return null;
        }
    }

    private bool Live()
    {
        if (_code is null) return false;
        if (time.GetUtcNow() < _expires) return true;
        _code = null;
        return false;
    }
}
