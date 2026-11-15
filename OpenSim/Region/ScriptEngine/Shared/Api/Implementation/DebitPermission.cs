using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    /// <summary>
    /// GridGate: when a saved PERMISSION_DEBIT may come back (money review 2026-11-15, R-1).
    /// </summary>
    public static class DebitPermission
    {
        /// <summary>
        /// The permissions to restore from a script's saved state: <paramref name="mask"/>, without PERMISSION_DEBIT unless
        /// <paramref name="granter"/> is the object's <paramref name="owner"/> and the state comes from a running script
        /// (<paramref name="fromRunningScript"/>: a region start, a crossing or a teleport). A rez or an attach restores the
        /// state saved in the object's asset, which an inventory backup or a copy sale can bring from someone else, so the
        /// script has to ask the owner again.
        /// </summary>
        public static int Restored(int mask, UUID granter, UUID owner, bool fromRunningScript) =>
            (mask & ScriptBaseClass.PERMISSION_DEBIT) == 0 || (fromRunningScript && granter.Equals(owner))
                ? mask
                : mask & ~ScriptBaseClass.PERMISSION_DEBIT;
    }
}
