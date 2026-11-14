using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: a money module that sets its own classified fee says so here. The profile module then checks and charges
    /// that fee instead of the listing price the viewer sent, which a resident could set to 0. Kept apart from
    /// IMoneyModule so the other money modules don't change.
    /// </summary>
    public interface IClassifiedFeeModule
    {
        /// <summary>The fee for a new classified, never negative.</summary>
        int ClassifiedFee { get; }

        /// <summary>Whether <paramref name="agentID"/> can pay <see cref="ClassifiedFee"/>.</summary>
        bool ClassifiedFeeCovered(UUID agentID);
    }
}
