using System.Runtime.CompilerServices;
using Mono.Addins;

[assembly: Addin("GridGateMoney", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: InternalsVisibleTo("GridGate.Money.Tests")]
