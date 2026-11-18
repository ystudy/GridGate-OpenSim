using System.Xml;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    /// <summary>
    /// GridGate: when a saved PERMISSION_DEBIT may come back.
    /// </summary>
    public static class DebitPermission
    {
        /// <summary>
        /// The permissions to restore from a script's saved state: <paramref name="mask"/>, without PERMISSION_DEBIT unless
        /// <paramref name="granter"/> is the object's <paramref name="owner"/> and the state comes from this program's own
        /// running script (<paramref name="fromRunningScript"/>, see <see cref="FromRunningScript"/>). A rez or an attach
        /// restores the state saved in the object's asset, which an inventory backup or a copy sale can bring from someone
        /// else, so the script has to ask the owner again.
        /// </summary>
        public static int Restored(int mask, UUID granter, UUID owner, bool fromRunningScript) =>
            (mask & ScriptBaseClass.PERMISSION_DEBIT) == 0 || (fromRunningScript && granter.Equals(owner))
                ? mask
                : mask & ~ScriptBaseClass.PERMISSION_DEBIT;

        /// <summary>
        /// Whether a script's saved state comes from this program's own running script: a region start, a crossing or a
        /// teleport (<paramref name="source"/>), not a state from outside this program (<paramref name="stateFromOutside"/>,
        /// the object's <c>SceneObjectGroup.StateFromOutside</c>), and written by this version of the program
        /// (<paramref name="stateIsMarked"/>, see <see cref="IsMarked"/>): a state saved before the mark existed brings
        /// no grant back. Another program's crossing or teleport, and an archive loaded as at a region start, can name
        /// anyone as the granter, and can copy the mark too, so the mark never lets a state from outside through.
        /// </summary>
        public static bool FromRunningScript(StateSource source, bool stateFromOutside, bool stateIsMarked) =>
            !stateFromOutside
            && stateIsMarked
            && (source == StateSource.RegionStart || source == StateSource.PrimCrossing || source == StateSource.Teleporting);

        /// <summary>The attribute on a saved state's <c>ScriptState</c> element that says this program wrote it.</summary>
        public const string MarkAttribute = "GridGateState";

        private const string MarkValue = "1";

        /// <summary>Marks a <c>ScriptState</c> element as written by this version of the program.</summary>
        public static void Mark(XmlElement scriptState) => scriptState.SetAttribute(MarkAttribute, MarkValue);

        /// <summary>Whether a <c>ScriptState</c> element carries this program's mark.</summary>
        public static bool IsMarked(XmlElement scriptState) => scriptState.GetAttribute(MarkAttribute) == MarkValue;
    }
}
