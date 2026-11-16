using System.Text.Json.Serialization;

namespace GridGate.Launcher.Core.Money;

/// <summary>The grid's money system, as the owner chose it (money design §1); saved by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MoneyMode>))]
public enum MoneyMode { Off, Play, Real, Gloebit }

/// <summary>Which hypergrid visitors may pay and be paid (verified-regions design §8.6); saved by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VisitorMoney>))]
public enum VisitorMoney { Off, Listed, All }

/// <summary>
/// A Region Server paired with this Grid Asset Server's money (money plan 3): the address it paired from, which is the
/// address its regions register with, its first region port, and the SHA-256 of its key. The key itself is never kept
/// here. Region Servers behind one router share an address and have their own port ranges, so a server's key covers
/// that address from <paramref name="FirstPort"/> up to the next paired server's first port there (final review,
/// Important 2).
/// </summary>
public sealed record PairedServer(Guid Id, string Host, string KeyHash, DateTimeOffset PairedAt, int FirstPort = 0, string? MachineId = null)
{
    /// <summary>The upper-case hex SHA-256 of a key's UTF-8 bytes, as stored.</summary>
    public static string HashOf(string key) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
}

/// <summary>
/// A Region Server's own side of its pairing (money plan 3): which Grid Asset Server, the money certificate fingerprint it
/// pinned, the key its regions use, and when. Kept in the owner-only settings file, like the local program keys.
/// </summary>
public sealed record ServerPairing(string GridHost, string Fingerprint, string Key, DateTimeOffset PairedAt);

/// <summary>
/// The owner's money settings (money design §7.5). <paramref name="ProgramKeys"/> maps a region program's instance to its
/// region key (§7.2); null reads as none.
/// </summary>
public sealed record MoneySettings(
    MoneyMode Mode = MoneyMode.Off,
    string CurrencySymbol = "C$",
    string CurrencyName = "coins",
    long StartingBalancePlay = 100,
    long StartingBalanceReal = 0,
    long WeeklyAllowance = 0,
    long UploadFee = 0,
    long ClassifiedFee = 0,
    bool ScriptPayouts = true,
    long DailyPayoutLimit = 5000,
    bool VisitorWallets = true,
    long MaxPayment = 1_000_000,
    Dictionary<string, string>? ProgramKeys = null,
    List<PairedServer>? PairedServers = null,
    ServerPairing? Pairing = null,
    GloebitSettings? Gloebit = null,
    VisitorMoney? Visitors = null,
    List<string>? ListedGrids = null,
    long DailySpendLimit = 10_000,
    long DailyKeySpendLimit = 100_000,
    long DailyKeyPayoutLimit = 20_000)
{
    private static readonly IReadOnlyDictionary<string, string> NoKeys = new Dictionary<string, string>();

    /// <summary><see cref="ProgramKeys"/>, never null.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Keys => ProgramKeys ?? NoKeys;

    /// <summary>
    /// Whose visitors may use money: <see cref="Visitors"/> when set, else what an older file's <see cref="VisitorWallets"/>
    /// meant (on read as only listed grids, off as none).
    /// </summary>
    [JsonIgnore]
    public VisitorMoney EffectiveVisitors => Visitors ?? (VisitorWallets ? VisitorMoney.Listed : VisitorMoney.Off);

    /// <summary>Whether <paramref name="home"/> is one of <see cref="ListedGrids"/>, compared as the ledger compares homes.</summary>
    public bool GridListed(string home)
    {
        var wanted = MoneyHomes.Normal(home);
        return !string.IsNullOrEmpty(wanted) && (ListedGrids ?? []).Any(g => MoneyHomes.Normal(g) == wanted);
    }

    /// <summary>What a new resident starts with in <paramref name="effective"/> mode; 0 unless Play or Real.</summary>
    public long StartingBalance(MoneyMode effective) => effective switch
    {
        MoneyMode.Play => StartingBalancePlay,
        MoneyMode.Real => StartingBalanceReal,
        _ => 0,
    };
}
