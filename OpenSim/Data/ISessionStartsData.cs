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
    }

    /// <summary>
    /// GridGate: the region each session started in, written only by Robust's login service and Gatekeeper
    /// (no HTTP route writes it), so a reader can trust it.
    /// </summary>
    public interface ISessionStartsData
    {
        /// <param name="source">"login" or "gatekeeper"</param>
        void Record(UUID sessionID, UUID userID, UUID regionID, string source);

        /// <summary>Deletes rows older than one day.</summary>
        void DeleteOld();
    }
}
