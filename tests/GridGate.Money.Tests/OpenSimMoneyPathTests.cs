using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GridGate.Region.Money;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Agent.AssetTransaction;
using OpenSim.Region.CoreModules.Avatar.UserProfiles;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Tests.Common;
using Xunit;
using static GridGate.Money.Tests.MoneyScene;

namespace GridGate.Money.Tests;

/// <summary>
/// GridGate's two other changes to OpenSim's money paths (money design §7.8), run against OpenSim's own modules with
/// GridGate's money module: UDP upload charging (AssetXferUploader) and the classified fee (UserProfileModule). Money
/// review 2026-11-15, R-10.
/// </summary>
[Collection(SceneCollection.Name)]
public sealed class OpenSimMoneyPathTests
{
    private static MoneyAnswer Balance(long coins) => new("ok", coins, 0, 1, 7);

    // --- UDP uploads (commit 5cf5b706) -------------------------------------------------------------------------------

    private const int UploadFee = 10;

    private static (MoneyScene Region, AssetTransactionModule Uploads, RecordingClient Uploader) UploadRegion(long balance)
    {
        var uploads = new AssetTransactionModule();
        var region = Build(new EconomyInfo("C$", UploadFee, 0, true, true, 1_000_000), others: [uploads]);
        region.Service.Answer = _ => Balance(balance);
        return (region, uploads, region.Arrive(Buyer));
    }

    /// <summary>
    /// The viewer's AssetUploadRequest with the texture in it, then the CreateInventoryItem that names its transaction
    /// (which the inventory access module hands to the upload module).
    /// </summary>
    private static bool Upload(AssetTransactionModule uploads, RecordingClient uploader, bool temporary = false)
    {
        var transaction = TestHelpers.ParseTail(0x200);
        uploader.Raise(nameof(IClientAPI.OnAssetUploadRequest), uploader, TestHelpers.ParseTail(0x201), transaction,
            (sbyte)AssetType.Texture, new byte[64], false, temporary);
        return uploads.HandleItemCreationFromTransaction(uploader, transaction, UUID.Zero, 1, "", "A texture",
            (sbyte)InventoryType.Texture, (sbyte)AssetType.Texture, 0, (uint)OpenSim.Framework.PermissionMask.All);
    }

    private static List<InventoryItemBase> Textures(MoneyScene region) =>
        region.Scene.InventoryService.GetFolderContent(Buyer, region.Scene.InventoryService.GetFolderForType(Buyer, FolderType.Texture).ID)
            .Items.Where(i => i.Name == "A texture").ToList();

    /// <summary>money review 2026-11-15, R-10: a UDP upload asks the money module whether the fee is covered, then charges it.</summary>
    [Fact]
    public void A_udp_upload_checks_the_fee_is_covered_then_charges_it()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Assert.True(Upload(uploads, uploader));

        Assert.Single(Textures(region));
        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("upload_fee", charge.Args[0]);
        Assert.Equal(Buyer.Guid, ((MoneyWho)charge.Args[1]!).AgentId);
        Assert.Equal(uploader.SessionId.Guid, ((MoneyWho)charge.Args[1]!).SessionId);
        Assert.Equal((long)UploadFee, charge.Args[2]);
        lock (region.Service.Order) Assert.Equal(new[] { "balance", "charge" }, region.Service.Order);
    }

    /// <summary>money review 2026-11-15, R-10: an uploader who can't pay the fee gets no upload and pays nothing.</summary>
    [Fact]
    public void A_udp_upload_the_uploader_cannot_pay_for_is_refused_and_not_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: UploadFee - 1);

        Upload(uploads, uploader);

        uploader.WaitForAlert("Insufficient funds");
        Assert.Empty(Textures(region));
        Assert.Empty(region.Service.Of("charge"));
    }

    /// <summary>money review 2026-11-15, R-10: a temporary upload (a viewer's local preview) is neither checked nor charged.</summary>
    [Fact]
    public void A_temporary_udp_upload_is_not_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Upload(uploads, uploader, temporary: true);

        Assert.Empty(region.Service.Calls);
    }

    // --- classifieds (commit 9a45df55) -------------------------------------------------------------------------------

    private const int ClassifiedFee = 25;

    private static (MoneyScene Region, RecordingClient Lister, UserProfileModule Profiles, ProfileServiceStub Server) ClassifiedRegion(long balance)
    {
        // OpenSim's own HTTP clients, as a region server sets them up at start; the profile module talks JSON-RPC with them.
        if (WebUtil.SharedSocketsHttpHandler is null) WebUtil.SetupHTTPClients(false, false, null, 32);
        var server = new ProfileServiceStub();
        var profiles = new UserProfileModule();
        var config = Config();
        config.AddConfig("UserProfiles").Set("ProfileServiceURL", server.Uri);
        var region = BuildWith(config, new EconomyInfo("C$", 0, ClassifiedFee, true, true, 1_000_000), new UserManagementModule(), profiles);
        region.Service.Answer = _ => Balance(balance);
        return (region, region.Arrive(Buyer), profiles, server);
    }

    private static MoneyScene BuildWith(Nini.Config.IConfigSource config, EconomyInfo economy, params object[] others) =>
        Build(economy, others, config: config);

    /// <summary>The viewer's ClassifiedInfoUpdate for a new ad, with the listing price the resident typed.</summary>
    private static void List(RecordingClient lister, int viewerPrice) =>
        lister.Raise(nameof(IClientAPI.OnClassifiedInfoUpdate), TestHelpers.ParseTail(0x300), 1u, "An ad", "About the ad", UUID.Zero, 1u,
            UUID.Zero, new Vector3(128, 128, 25), (byte)0, viewerPrice, lister);

    /// <summary>
    /// money review 2026-11-15, R-10: a new classified is checked against and charged the money module's fee; a listing
    /// price of 0 doesn't make it free.
    /// </summary>
    [Fact]
    public void A_classified_is_checked_and_charged_at_the_grids_fee_not_the_viewers_price()
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: ClassifiedFee);
        using var _server = server;

        List(lister, viewerPrice: 0);

        Assert.Contains("classified_update", server.Methods);
        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("classified_fee", charge.Args[0]);
        Assert.Equal((long)ClassifiedFee, charge.Args[2]);
        lock (region.Service.Order) Assert.Equal(new[] { "balance", "charge" }, region.Service.Order);
    }

    /// <summary>
    /// money review 2026-11-15, R-10: a resident short of the grid's fee is refused, however low the price they typed;
    /// the ad isn't saved and nothing is charged.
    /// </summary>
    [Fact]
    public void A_classified_the_lister_cannot_pay_the_grids_fee_for_is_refused()
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: ClassifiedFee - 1);
        using var _server = server;

        List(lister, viewerPrice: 1);

        lister.WaitForAlert("You do not have enough money to create this classified.");
        Assert.DoesNotContain("classified_update", server.Methods);
        Assert.Empty(region.Service.Of("charge"));
    }
}

/// <summary>
/// The profile server, faked: answers OpenSim's JSON-RPC calls over plain HTTP on a loopback port. The resident has no
/// classifieds yet, and every update is accepted.
/// </summary>
internal sealed class ProfileServiceStub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public ConcurrentQueue<string> Methods { get; } = new();

    public string Uri { get; }

    public ProfileServiceStub()
    {
        _listener.Start();
        Uri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var head = new StringBuilder();
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var b = stream.ReadByte();
            if (b < 0) return;
            head.Append((char)b);
        }
        var length = head.ToString().Split("\r\n")
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim()))
            .FirstOrDefault();
        var body = new byte[length];
        for (var read = 0; read < length;)
        {
            var n = stream.Read(body, read, length - read);
            if (n <= 0) return;
            read += n;
        }

        var request = (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(body));
        var method = request["method"].AsString();
        Methods.Enqueue(method);
        OSD result = method == "avatarclassifiedsrequest" ? new OSDArray() : request["params"];
        var answer = OSDParser.SerializeJsonToBytes(new OSDMap { ["jsonrpc"] = "2.0", ["id"] = request["id"], ["result"] = result });
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json-rpc\r\nContent-Length: {answer.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(header);
        stream.Write(answer);
    }

    public void Dispose() => _listener.Stop();
}
