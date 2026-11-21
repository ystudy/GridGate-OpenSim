using System.Globalization;
using GridGate.Launcher.Core.Database;

namespace GridGate.Launcher.Guests;

/// <summary>One thing the avatar wears.</summary>
/// <param name="Type">The wearable type's resource key ("Avatar.WearShirt"); null for an attachment.</param>
/// <param name="Name">The item's name; null when it is no longer in their inventory.</param>
/// <param name="Creator">Who made it; null when unknown.</param>
public sealed record WornLine(string? Type, string? Name, string? Creator);

/// <summary>What the avatar wears, in the Avatar tab's four groups (spec 2026-11-21-avatar-page-design, "The Avatar tab").</summary>
public sealed record AvatarLook(IReadOnlyList<WornLine> Body, IReadOnlyList<WornLine> Clothes, IReadOnlyList<WornLine> Attached,
    IReadOnlyList<WornLine> Huds)
{
    public bool IsEmpty => Body.Count + Clothes.Count + Attached.Count + Huds.Count == 0;
}

public enum AvatarLookState
{
    /// <summary>Read: <see cref="AvatarLookResult.Look"/> holds it.</summary>
    Read,

    /// <summary>The grid has no stored look for them yet (they haven't been in-world).</summary>
    NotSaved,

    /// <summary>The database couldn't be asked.</summary>
    Unavailable,
}

public sealed record AvatarLookResult(AvatarLookState State, AvatarLook? Look = null);

/// <summary>
/// Reads what a resident's avatar wears (spec 2026-11-21-avatar-page-design): the stored appearance (<see cref="IAppearanceStore"/>,
/// as the starter-avatar check reads it) and the worn items' names and creators from their own inventory. Only ever asked about the
/// signed-in resident themself.
/// </summary>
public sealed class AvatarLooks(IAppearanceStore looks, IInventoryStore inventory)
{
    /// <summary>OpenSim's wearable types 0–3: shape, skin, hair, eyes.</summary>
    private const int LastBodyPart = 3;

    /// <summary>The viewer's HUD attachment points.</summary>
    private const int FirstHud = 31, LastHud = 38;

    // OpenMetaverse AssetType.Link and LinkFolder: an inventory link, whose AssetID is the item it points to.
    private const int Link = 24, LinkFolder = 25;

    /// <summary>The resource keys of OpenSim's wearable types, by number.</summary>
    public static readonly IReadOnlyList<string> TypeKeys =
    [
        "Avatar.WearShape", "Avatar.WearSkin", "Avatar.WearHair", "Avatar.WearEyes", "Avatar.WearShirt", "Avatar.WearPants",
        "Avatar.WearShoes", "Avatar.WearSocks", "Avatar.WearJacket", "Avatar.WearGloves", "Avatar.WearUndershirt",
        "Avatar.WearUnderpants", "Avatar.WearSkirt", "Avatar.WearAlpha", "Avatar.WearTattoo", "Avatar.WearPhysics",
        "Avatar.WearUniversal",
    ];

    public async Task<AvatarLookResult> ReadAsync(Guid principal, CancellationToken ct)
    {
        try
        {
            var worn = await looks.WornAsync(principal, ct);
            if (worn is null) return new(AvatarLookState.NotSaved);
            var items = (await inventory.ItemsAsync(principal, [.. worn.Select(w => w.ItemId).Distinct()], ct)).ToList();
            // A worn link (the viewer's outfit folders) names the item it points to: read those too.
            var targets = items.Where(i => i.Row.AssetType is Link or LinkFolder && Guid.TryParse(i.Row.AssetId, out _))
                .Select(i => Guid.Parse(i.Row.AssetId!)).Where(id => items.All(x => x.Row.ItemId != id)).Distinct().ToList();
            if (targets.Count > 0) items.AddRange(await inventory.ItemsAsync(principal, targets, ct));
            return new(AvatarLookState.Read, Group(worn, items));
        }
        catch (StaffDirectoryUnavailableException)
        {
            return new(AvatarLookState.Unavailable);
        }
    }

    /// <summary>Sorts the worn items into the four groups, each in the viewer's order (type, then layer; point, then item).</summary>
    public static AvatarLook Group(IReadOnlyList<WornItem> worn, IReadOnlyList<InventoryItemView> items)
    {
        var byId = items.GroupBy(i => i.Row.ItemId).ToDictionary(g => g.Key, g => g.First());
        WornLine Line(Guid id, string? type)
        {
            if (!byId.TryGetValue(id, out var item)) return new(type, null, null);
            if (item.Row.AssetType is Link or LinkFolder && Guid.TryParse(item.Row.AssetId, out var target) && byId.TryGetValue(target, out var real))
                item = real;
            var name = item.Row.Name?.Trim();
            return new(type, string.IsNullOrEmpty(name) ? null : name, string.IsNullOrWhiteSpace(item.CreatorName) ? null : item.CreatorName.Trim());
        }

        var body = new List<(int, int, WornLine)>();
        var clothes = new List<(int, int, WornLine)>();
        var attached = new List<(int, int, WornLine)>();
        var huds = new List<(int, int, WornLine)>();
        var order = 0;
        foreach (var w in worn)
        {
            order++;
            if (Wearable(w.Slot) is var (type, layer))
            {
                var line = Line(w.ItemId, type < TypeKeys.Count ? TypeKeys[type] : null);
                (type <= LastBodyPart ? body : clothes).Add((type, layer, line));
            }
            else if (Point(w.Slot) is int point)
                (point is >= FirstHud and <= LastHud ? huds : attached).Add((point, order, Line(w.ItemId, null)));
        }
        static IReadOnlyList<WornLine> Sorted(List<(int A, int B, WornLine L)> list) => [.. list.OrderBy(x => x.A).ThenBy(x => x.B).Select(x => x.L)];
        return new AvatarLook(Sorted(body), Sorted(clothes), Sorted(attached), Sorted(huds));
    }

    /// <summary><c>Wearable i:j</c> as (type i, layer j); null for anything else.</summary>
    private static (int Type, int Layer)? Wearable(string slot)
    {
        if (!slot.StartsWith("Wearable ", StringComparison.Ordinal)) return null;
        var parts = slot["Wearable ".Length..].Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var i)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var j)
            ? (i, j)
            : null;
    }

    /// <summary><c>_ap_N</c> as N; null for anything else.</summary>
    private static int? Point(string slot) =>
        slot.StartsWith("_ap_", StringComparison.Ordinal)
        && int.TryParse(slot[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
