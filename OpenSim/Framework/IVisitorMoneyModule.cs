using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: a money module that can pay hypergrid visitors says so here. llTransferLindenDollars asks it about a
    /// recipient that has no account on this grid, instead of refusing every visitor. Kept apart from IMoneyModule so the
    /// other money modules don't change.
    /// </summary>
    public interface IVisitorMoneyModule
    {
        /// <summary>Whether <paramref name="agentID"/>, a visitor from another grid, can be paid here.</summary>
        bool CanPayVisitor(UUID agentID);
    }
}
