using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: lets one paid group join at a time per person and group. Two joins or accepts by the same person for
    /// the same group would both find them not yet a member and both charge the join fee; the second one is turned away
    /// while the first is still adding them. Safe to use from many threads.
    /// </summary>
    public sealed class JoinsInProgress
    {
        private readonly object m_lock = new();
        private readonly HashSet<(UUID Agent, UUID Group)> m_active = new();

        /// <summary>True when this person's join to this group may go ahead; false when another is in progress.</summary>
        public bool TryBegin(UUID agentID, UUID groupID)
        {
            lock (m_lock)
                return m_active.Add((agentID, groupID));
        }

        /// <summary>The join that began ends, whatever came of it.</summary>
        public void End(UUID agentID, UUID groupID)
        {
            lock (m_lock)
                m_active.Remove((agentID, groupID));
        }
    }
}
