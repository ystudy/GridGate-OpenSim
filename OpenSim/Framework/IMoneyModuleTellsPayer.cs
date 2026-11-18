namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: marks a money module whose <c>MoveMoney</c> tells the payer why a payment failed (too few coins, the
    /// payment is being checked, money is unavailable). The land module then adds no "try again later" of its own after a
    /// parcel pass payment fails, which for a payment still being checked would invite paying twice.
    /// </summary>
    public interface IMoneyModuleTellsPayer
    {
    }
}
