using System.Runtime.CompilerServices;
using Mono.Addins;

[assembly: Addin("GridGateTraffic", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
[assembly: InternalsVisibleTo("GridGate.Traffic.Tests")]
