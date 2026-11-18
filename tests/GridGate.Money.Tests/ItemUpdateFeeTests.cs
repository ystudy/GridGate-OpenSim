using GridGate.Region.Money;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static GridGate.Money.Tests.MoneyScene;

namespace GridGate.Money.Tests;

/// <summary>
/// Updating the asset of an existing item through the viewer's update capabilities can't store texture or sound data
/// for free while an upload fee is in force: those are made by uploads, which pay.
/// </summary>
[Collection(SceneCollection.Name)]
public sealed class ItemUpdateFeeTests
{
    private const int UploadFee = 10;

    private static readonly byte[] TextureData = [0xFF, 0x4F, 0xFF, 0x51, .. new byte[60]];
    private static readonly byte[] SoundData = [.. "OggS"u8, .. new byte[24], 0x01, .. "vorbis"u8, .. new byte[20]];
    private static readonly byte[] NotecardData = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 5\nhello}\n"u8.ToArray();

    private static (MoneyScene Region, RecordingClient Client) Region(int fee = UploadFee)
    {
        var config = Config();
        config.Configs["Modules"].Set("InventoryAccessModule", "BasicInventoryAccessModule");
        var region = Build(new EconomyInfo("C$", fee, 0, true, true, 1_000_000), others: [new BasicInventoryAccessModule()], config: config);
        return (region, region.Arrive(Buyer));
    }

    private static InventoryItemBase AddItem(MoneyScene region, AssetType type, InventoryType invType, FolderType folder)
    {
        var item = new InventoryItemBase(UUID.Random(), Buyer)
        {
            Name = "An item", AssetType = (int)type, InvType = (int)invType,
            Folder = region.Scene.InventoryService.GetFolderForType(Buyer, folder).ID,
            BasePermissions = (uint)OpenSim.Framework.PermissionMask.All, CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        };
        Assert.True(region.Scene.InventoryService.AddItem(item));
        return item;
    }

    private static void AssertRefusedAndUnchanged(MoneyScene region, RecordingClient client, UUID result, InventoryItemBase item, UUID oldAsset)
    {
        Assert.Equal(UUID.Zero, result);
        Assert.Contains(UploadData.ItemUpdateRefusedText, client.Alerts);
        Assert.Equal(oldAsset, region.Scene.InventoryService.GetItem(Buyer, item.ID).AssetID);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Texture_or_sound_data_posted_as_a_notecard_update_is_refused(bool sound)
    {
        var (region, client) = Region();
        var item = AddItem(region, AssetType.Notecard, InventoryType.Notecard, FolderType.Notecard);

        var result = region.Scene.CapsUpdateItemAsset(Buyer, item.ID, UUID.Zero, sound ? SoundData : TextureData);

        AssertRefusedAndUnchanged(region, client, result, item, UUID.Zero);
    }

    [Fact]
    public void Texture_data_posted_onto_a_texture_item_is_refused()
    {
        var (region, client) = Region();
        var item = AddItem(region, AssetType.Texture, InventoryType.Texture, FolderType.Texture);
        var oldAsset = TestHelpers.ParseTail(0x602);
        item.AssetID = oldAsset;
        Assert.True(region.Scene.InventoryService.UpdateItem(item));

        var result = region.Scene.CapsUpdateItemAsset(Buyer, item.ID, UUID.Zero, TextureData);

        AssertRefusedAndUnchanged(region, client, result, item, oldAsset);
    }

    [Fact]
    public void A_notecards_text_can_still_be_updated()
    {
        var (region, client) = Region();
        var item = AddItem(region, AssetType.Notecard, InventoryType.Notecard, FolderType.Notecard);

        var result = region.Scene.CapsUpdateItemAsset(Buyer, item.ID, UUID.Zero, NotecardData);

        Assert.NotEqual(UUID.Zero, result);
        Assert.Equal(result, region.Scene.InventoryService.GetItem(Buyer, item.ID).AssetID);
        Assert.NotNull(region.Scene.AssetService.Get(result.ToString()));
        Assert.DoesNotContain(UploadData.ItemUpdateRefusedText, client.Alerts);
    }

    [Fact]
    public void Without_an_upload_fee_texture_data_posted_as_a_notecard_update_is_stored_as_before()
    {
        var (region, client) = Region(fee: 0);
        var item = AddItem(region, AssetType.Notecard, InventoryType.Notecard, FolderType.Notecard);

        var result = region.Scene.CapsUpdateItemAsset(Buyer, item.ID, UUID.Zero, TextureData);

        Assert.NotEqual(UUID.Zero, result);
        Assert.Equal(result, region.Scene.InventoryService.GetItem(Buyer, item.ID).AssetID);
        Assert.NotNull(region.Scene.AssetService.Get(result.ToString()));
        Assert.DoesNotContain(UploadData.ItemUpdateRefusedText, client.Alerts);
    }

    [Fact]
    public void Texture_data_posted_as_a_task_item_update_is_refused()
    {
        var (region, client) = Region();
        var part = SceneHelpers.AddSceneObject(region.Scene, 1, Buyer, "Box ", 0x50).RootPart;
        var oldAsset = TestHelpers.ParseTail(0x603);
        var task = TaskInventoryHelpers.AddNotecard(region.Scene.AssetService, part, "A notecard", TestHelpers.ParseTail(0x604), oldAsset, "hello");

        var result = region.Scene.CapsUpdateItemAsset(Buyer, task.ItemID, part.UUID, TextureData);

        Assert.Equal(UUID.Zero, result);
        Assert.Contains(UploadData.ItemUpdateRefusedText, client.Alerts);
        Assert.Equal(oldAsset, part.Inventory.GetInventoryItem(task.ItemID).AssetID);
    }

    private static (MoneyScene Region, RecordingClient Client, SceneObjectPart Part, TaskInventoryItem Script) ScriptRegion()
    {
        var (region, client) = Region();
        var part = SceneHelpers.AddSceneObject(region.Scene, 1, Buyer, "Box ", 0x50).RootPart;
        var script = TaskInventoryHelpers.AddScript(region.Scene.AssetService, part, TestHelpers.ParseTail(0x605), TestHelpers.ParseTail(0x606), "A script", "default { state_entry() { } }");
        return (region, client, part, script);
    }

    [Fact]
    public void Texture_data_posted_as_a_task_script_update_is_refused_and_not_stored()
    {
        var (region, client, part, script) = ScriptRegion();
        var oldAsset = script.AssetID;

        var errors = region.Scene.CapsUpdateTaskInventoryScriptAsset(Buyer, script.ItemID, part.UUID, true, TextureData);

        Assert.Equal([UploadData.ItemUpdateRefusedText], errors.Cast<object>());
        Assert.Contains(UploadData.ItemUpdateRefusedText, client.Alerts);
        Assert.Equal(oldAsset, part.Inventory.GetInventoryItem(script.ItemID).AssetID);
    }

    /// <summary>Compiling is not asserted: only that the new text was stored and the item points at it.</summary>
    [Fact]
    public void A_task_scripts_text_can_still_be_updated()
    {
        var (region, client, part, script) = ScriptRegion();
        var oldAsset = script.AssetID;

        region.Scene.CapsUpdateTaskInventoryScriptAsset(Buyer, script.ItemID, part.UUID, false, "default { state_entry() { } }"u8.ToArray());

        var now = part.Inventory.GetInventoryItem(script.ItemID).AssetID;
        Assert.NotEqual(oldAsset, now);
        Assert.NotNull(region.Scene.AssetService.Get(now.ToString()));
        Assert.DoesNotContain(UploadData.ItemUpdateRefusedText, client.Alerts);
    }

    [Fact]
    public void A_task_notecards_text_can_still_be_updated()
    {
        var (region, _) = Region();
        var part = SceneHelpers.AddSceneObject(region.Scene, 1, Buyer, "Box ", 0x50).RootPart;
        var task = TaskInventoryHelpers.AddNotecard(region.Scene.AssetService, part, "A notecard", TestHelpers.ParseTail(0x604), TestHelpers.ParseTail(0x603), "hello");

        var result = region.Scene.CapsUpdateItemAsset(Buyer, task.ItemID, part.UUID, NotecardData);

        Assert.NotEqual(UUID.Zero, result);
        Assert.Equal(result, part.Inventory.GetInventoryItem(task.ItemID).AssetID);
    }
}
