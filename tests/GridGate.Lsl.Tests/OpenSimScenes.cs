using Xunit;

namespace GridGate.Lsl.Tests;

/// <summary>
/// The test classes that set up OpenSim test regions. They run one after another: OpenSim keeps some of a region's
/// surroundings in static state (the scene manager <see cref="OpenSim.Tests.Common.SceneHelpers"/> replaces with each
/// new instance, the console), which one test's region setup would change under another's.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OpenSimScenes
{
    public const string Name = "OpenSim scenes";
}
