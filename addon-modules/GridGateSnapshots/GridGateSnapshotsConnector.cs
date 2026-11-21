using System.IO;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;

namespace GridGate.Robust.Snapshots
{
    /// <summary>
    /// Robust's <c>POST /gridgate/j2k</c> (spec 2026-11-21-featured-places-design, "Pictures"), listed in Robust's
    /// <c>[ServiceList]</c> on the private port, so only the launcher (and other private services) can reach it. It holds no
    /// asset service: the launcher fetches the texture and sends its bytes.
    /// </summary>
    public sealed class GridGateSnapshotsConnector : ServiceConnector
    {
        public GridGateSnapshotsConnector(IConfigSource config, IHttpServer server, string configName)
            : base(config, server, configName) =>
            server.AddStreamHandler(new ConvertHandler());

        private sealed class ConvertHandler : BaseStreamHandler
        {
            public ConvertHandler() : base("POST", "/gridgate/j2k")
            {
            }

            protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
            {
                var (status, body) = SnapshotConverter.Answer(request);
                httpResponse.StatusCode = status;
                if (status == 200) httpResponse.ContentType = "image/jpeg";
                return body;
            }
        }
    }
}
