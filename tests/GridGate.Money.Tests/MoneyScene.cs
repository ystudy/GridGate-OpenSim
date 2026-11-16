using System.Collections.Concurrent;
using System.Reflection;
using GridGate.Region.Money;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>
/// The tests that build an OpenSim test region. OpenSim's test helpers share statics (the console, the presence data,
/// the scene manager), so these never run alongside each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SceneCollection
{
    public const string Name = "OpenSim test region";
}

/// <summary>
/// A test region with <see cref="GridGateMoneyModule"/> loaded the way OpenSim loads it, talking to a
/// <see cref="FakeMoneyService"/> instead of the launcher's money service.
/// </summary>
/// <remarks>
/// The module builds its own service client in <c>Initialise</c> from the INI and keeps it in a private field; there is
/// no way to hand it one. The fake is put in that field between <c>Initialise</c> and <c>AddRegion</c>, which is when
/// the module first uses it (<c>AddRegion</c> gives it to the region's <see cref="MoneyActions"/>).
/// </remarks>
internal sealed class MoneyScene
{
    public static readonly UUID Buyer = TestHelpers.ParseTail(0x11);
    public static readonly UUID Seller = TestHelpers.ParseTail(0x12);
    public static readonly UUID Stranger = TestHelpers.ParseTail(0x13);
    public static readonly UUID GroupId = TestHelpers.ParseTail(0x14);
    public static readonly UUID Founder = TestHelpers.ParseTail(0x15);

    public required Scene Scene { get; init; }
    public required GridGateMoneyModule Module { get; init; }
    public required FakeMoneyService Service { get; init; }

    /// <summary>The INI that turns the module on, as the launcher writes it while money is on.</summary>
    public static IniConfigSource Config()
    {
        var source = new IniConfigSource();
        source.AddConfig("Economy").Set("EconomyModule", GridGateMoneyModule.ModuleName);
        source.AddConfig("Modules").Set("UserManagementModule", "BasicUserManagementModule");
        var money = source.AddConfig("GridGateMoney");
        money.Set("ServiceURI", "https://127.0.0.1:8000/money/v1/");
        money.Set("RegionKey", "k");
        money.Set("ServiceFingerprint", new string('A', 64));
        money.Set("HelperURI", "http://example.test:8001/economy/");
        return source;
    }

    /// <summary>
    /// A region with <paramref name="others"/> (OpenSim's own modules) loaded first and the money module last, as the
    /// money module's own handlers expect to run after the land module's. <paramref name="beforeMoney"/> runs between
    /// the two: what it hooks runs before the money module's handlers. <paramref name="config"/> defaults to <see cref="Config"/>.
    /// </summary>
    public static MoneyScene Build(EconomyInfo? economy = null, object[]? others = null, Action<Scene>? beforeMoney = null,
        IConfigSource? config = null)
    {
        config ??= Config();
        var scene = new SceneHelpers().SetupScene("Money test region", TestHelpers.ParseTail(0x100), 1000, 1000, config);
        if (others is { Length: > 0 }) SceneHelpers.SetupSceneModules(scene, config, others);
        beforeMoney?.Invoke(scene);

        var service = new FakeMoneyService { Economy = economy ?? new EconomyInfo("C$", 0, 0, true, true, 1_000_000) };
        var module = NewModule(service, config);
        AddTo(module, scene);

        var region = new MoneyScene { Scene = scene, Module = module, Service = service };
        region.WaitForEconomy(service.Economy!);
        return region;
    }

    /// <summary>The module, initialised from <paramref name="config"/> (default <see cref="Config"/>), talking to <paramref name="service"/>.</summary>
    public static GridGateMoneyModule NewModule(FakeMoneyService service, IConfigSource? config = null)
    {
        var module = new GridGateMoneyModule();
        module.Initialise(config ?? Config());
        Assert.True(module.Enabled);
        var field = typeof(GridGateMoneyModule).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("GridGateMoneyModule no longer keeps its client in _client.");
        (field.GetValue(module) as IDisposable)?.Dispose();
        field.SetValue(module, service);
        module.PostInitialise();
        return module;
    }

    /// <summary>Loads <paramref name="module"/> into <paramref name="scene"/> as OpenSim loads a shared module.</summary>
    public static void AddTo(GridGateMoneyModule module, Scene scene)
    {
        module.AddRegion(scene);
        scene.AddRegionModule(module.Name, module);
        module.RegionLoaded(scene);
        scene.SetModuleInterfaces();
    }

    /// <summary>Waits until the module has sent every place report queued so far.</summary>
    public void FlushPlaces() => Flush(Module);

    public static void Flush(GridGateMoneyModule module) =>
        Assert.True(module.Places!.FlushAsync().Wait(TimeSpan.FromSeconds(15)), "The module's place reports never went out.");

    /// <summary>The module asks the service for its settings in the background; waits until it has them.</summary>
    private void WaitForEconomy(EconomyInfo economy)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Module.UploadCharge != economy.UploadFee || ((IClassifiedFeeModule)Module).ClassifiedFee != economy.ClassifiedFee)
        {
            Assert.True(DateTime.UtcNow < deadline, "The module never read the economy settings.");
            Thread.Sleep(10);
        }
    }

    /// <summary>A resident with an account and inventory, in the region as a root agent, on a client that records alerts.</summary>
    public RecordingClient Arrive(UUID id)
    {
        if (Scene.UserAccountService.GetUserAccount(Scene.RegionInfo.ScopeID, id) is null)
            UserAccountHelpers.CreateUserWithInventory(Scene, id);
        var data = SceneHelpers.GenerateAgentData(id);
        var client = new RecordingClient(data, Scene);
        SceneHelpers.AddScenePresence(Scene, client, data);
        return client;
    }

    /// <summary>Waits until the service has had a call to <paramref name="method"/> matching <paramref name="match"/>.</summary>
    public Call WaitFor(string method, Func<Call, bool>? match = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var call = Service.Of(method).FirstOrDefault(c => match is null || match(c));
            if (call is not null) return call;
            Assert.True(DateTime.UtcNow < deadline, $"No {method} call reached the money service.");
            Thread.Sleep(10);
        }
    }

    /// <summary>Waits until the service has had a place report of <paramref name="method"/>.</summary>
    public Call WaitForPlace(string method)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (Service.PlacesOf(method).FirstOrDefault() is { } call) return call;
            Assert.True(DateTime.UtcNow < deadline, $"No {method} report reached the money service.");
            Thread.Sleep(10);
        }
    }

    /// <summary>The service's calls of <paramref name="method"/>, by the first argument (the transfer, hold or charge type).</summary>
    public IEnumerable<Call> Of(string method, string type) => Service.Of(method).Where(c => (string?)c.Args[0] == type);
}

/// <summary>
/// OpenSim's test client, keeping the alerts the region sends it. <see cref="TestClient"/>'s alert methods aren't
/// virtual; re-implementing <see cref="IClientAPI"/> puts these in their place for everything that calls through it.
/// </summary>
internal sealed class RecordingClient(AgentCircuitData data, Scene scene) : TestClient(data, scene), IClientAPI
{
    public ConcurrentQueue<string> Alerts { get; } = new();

    public new void SendAlertMessage(string message) => Alerts.Enqueue(message);

    public new void SendAgentAlertMessage(string message, bool modal) => Alerts.Enqueue(message);

    /// <summary>Raises one of the client's packet events, as OpenSim's UDP stack does when the viewer's packet arrives.</summary>
    public void Raise(string clientEvent, params object?[] args)
    {
        var field = typeof(TestClient).GetField(clientEvent, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"TestClient has no {clientEvent} event.");
        var handler = field.GetValue(this) as Delegate ?? throw new InvalidOperationException($"Nothing listens to {clientEvent}.");
        handler.DynamicInvoke(args);
    }

    /// <summary>Waits until an alert containing <paramref name="text"/> arrives.</summary>
    public void WaitForAlert(string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!Alerts.Any(a => a.Contains(text, StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < deadline, $"No alert saying \"{text}\" arrived. Alerts: {string.Join(" | ", Alerts)}");
            Thread.Sleep(10);
        }
    }
}

/// <summary>A groups module that knows one group, for the founder a group join fee is paid to.</summary>
internal class OneGroup : DispatchProxy
{
    public static IGroupsModule Create(string name, UUID founder)
    {
        var proxy = Create<IGroupsModule, OneGroup>();
        var self = (OneGroup)(object)proxy;
        self._record = new GroupRecord { GroupID = MoneyScene.GroupId, GroupName = name, FounderID = founder };
        return proxy;
    }

    private GroupRecord? _record;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == nameof(IGroupsModule.GetGroupRecord) && args is [string name]
            ? name == _record!.GroupName ? _record : null
            : throw new NotSupportedException(targetMethod?.Name);
}
