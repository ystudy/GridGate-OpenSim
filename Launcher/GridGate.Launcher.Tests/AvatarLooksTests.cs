using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Guests;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>What the Avatar tab says the avatar wears (spec 2026-11-21-avatar-page-design, "The Avatar tab").</summary>
public sealed class AvatarLooksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Ann = Guid.NewGuid();

    private static InventoryItemView View(Guid id, string name, string creator = "Maker Resident", int assetType = 13, string? asset = null) =>
        new(StarterCheckTests.Row(id, Ann, name, Guid.NewGuid()) with { AssetType = assetType, AssetId = asset ?? Guid.NewGuid().ToString() },
            creator, "My Inventory");

    [Fact]
    public void Groups_body_clothes_attachments_and_huds_in_the_viewers_order()
    {
        var (shape, skin, shirt, alpha, jacket, hat, hud, ring, gone) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        WornItem[] worn =
        [
            new("Wearable 1:0", skin), new("Wearable 0:0", shape), new("Wearable 13:0", alpha), new("Wearable 4:0", shirt),
            new("Wearable 8:0", jacket), new("_ap_2", hat), new("_ap_33", hud), new("_ap_41", ring), new("_ap_5", gone),
        ];
        var items = new[]
        {
            View(shape, "Tall shape"), View(skin, "Sun skin"), View(shirt, "Blue shirt", "Ann Sailor"), View(alpha, "Feet alpha"),
            View(jacket, "Rain jacket"), View(hat, "Captain hat", assetType: 6), View(hud, "Dance HUD", assetType: 6),
            View(ring, "Ring", assetType: 6),
        };

        var look = AvatarLooks.Group(worn, items);

        Assert.Equal([("Avatar.WearShape", "Tall shape"), ("Avatar.WearSkin", "Sun skin")], look.Body.Select(l => (l.Type, l.Name)));
        Assert.Equal(["Avatar.WearShirt", "Avatar.WearJacket", "Avatar.WearAlpha"], look.Clothes.Select(l => l.Type));
        Assert.Equal("Ann Sailor", look.Clothes[0].Creator);
        Assert.Equal(["Captain hat", null, "Ring"], look.Attached.Select(l => l.Name)); // points 2, 5 (gone), 41
        Assert.Equal(["Dance HUD"], look.Huds.Select(l => l.Name));
        Assert.All(look.Attached, l => Assert.Null(l.Type));
    }

    [Fact]
    public void A_worn_link_shows_the_item_it_points_to()
    {
        var (link, real) = (Guid.NewGuid(), Guid.NewGuid());
        var look = AvatarLooks.Group([new("_ap_2", link)],
            [View(link, "Hat (link)", assetType: 24, asset: real.ToString()), View(real, "Captain hat", "Hat Maker")]);
        Assert.Equal(("Captain hat", "Hat Maker"), (look.Attached[0].Name, look.Attached[0].Creator));
    }

    [Fact]
    public void Odd_slots_are_left_out()
    {
        var look = AvatarLooks.Group([new("Wearable x:0", Guid.NewGuid()), new("_ap_", Guid.NewGuid()), new("Other", Guid.NewGuid())], []);
        Assert.True(look.IsEmpty);
    }

    [Fact]
    public async Task Read_asks_their_own_inventory_and_follows_links()
    {
        var robust = new FakeRobustAccounts();
        var inventory = new FakeInventoryStore(robust);
        var folder = inventory.AddDefaultFolders(Ann);
        var real = inventory.Add(Ann, folder, "Captain hat", assetType: 6);
        var link = inventory.Add(Ann, folder, "Captain hat", assetId: real.ItemId.ToString(), assetType: 24);
        var looks = new FakeAppearanceStore();
        looks.Set(Ann, [new WornItem("_ap_2", link.ItemId)]);

        var result = await new AvatarLooks(looks, inventory).ReadAsync(Ann, Ct);

        Assert.Equal(AvatarLookState.Read, result.State);
        Assert.Equal("Captain hat", Assert.Single(result.Look!.Attached).Name);
    }

    [Fact]
    public async Task Not_saved_yet_and_unavailable()
    {
        var inventory = new FakeInventoryStore(new FakeRobustAccounts());
        var looks = new FakeAppearanceStore();
        Assert.Equal(AvatarLookState.NotSaved, (await new AvatarLooks(looks, inventory).ReadAsync(Ann, Ct)).State);
        looks.Down = true;
        Assert.Equal(AvatarLookState.Unavailable, (await new AvatarLooks(looks, inventory).ReadAsync(Ann, Ct)).State);
    }
}
