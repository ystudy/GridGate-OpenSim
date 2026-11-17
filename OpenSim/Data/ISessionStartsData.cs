// GridGate: records where each session starts, for the money service. See MERGING.md.
using OpenMetaverse;

namespace OpenSim.Data
{
    /// <summary>One row of gridgate_session_starts. This MUST be a ref type.</summary>
    public class SessionStartRow
    {
        public UUID SessionID;
        public UUID UserID;
        public UUID RegionID;
        public string Source;
        /// <summary>The region's address and UDP port as Robust listed it (regions.serverIP, serverPort); null when unknown.</summary>
        public string ServerIP;
        public int? ServerPort;
    }

    /// <summary>
    /// GridGate: the region each session started in, and the address the session was sent to there, written only by
    /// Robust's login service and Gatekeeper (no HTTP route writes it), so a reader can trust it. The address tells the
    /// money service which server the start really went to, whatever the region id is registered at later.
    /// </summary>
    public interface ISessionStartsData
    {
        /// <param name="serverIP">the destination's ExternalHostName (Robust's regions.serverIP), or null when unknown</param>
        /// <param name="serverPort">the destination's UDP port (Robust's regions.serverPort), or null when unknown</param>
        /// <param name="source">"login" or "gatekeeper"</param>
        void Record(UUID sessionID, UUID userID, UUID regionID, string serverIP, int? serverPort, string source);

        /// <summary>Deletes rows older than one day.</summary>
        void DeleteOld();
    }
}
