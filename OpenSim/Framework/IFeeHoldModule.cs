using System.Threading;
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: a money module that can hold a fee before the action it pays for (a group join, a classified, a caps
    /// upload) says so here. The fee waits with the money service until the action is done: taken when it worked, given
    /// back when it didn't, so nobody pays for a join or an ad that failed, nor gets one that worked without paying. Kept
    /// apart from IMoneyModule so the other money modules don't change: <see cref="FeeHolds.Take"/> gives them today's
    /// check now, charge after.
    /// </summary>
    public interface IFeeHoldModule
    {
        /// <summary>
        /// Holds the fee before its action. Null when it can't be paid: the agent was told why, except for too few coins,
        /// which the caller says in its own words.
        /// </summary>
        IFeeHold HoldFee(UUID agentID, int amount, MoneyTransactionType type, string extraData = "");
    }

    /// <summary>GridGate: a fee held before its action.</summary>
    public interface IFeeHold
    {
        /// <summary>
        /// Once: true takes the fee, false gives it back. A fee the money service holds is settled without waiting for
        /// it; one only checked when taken (a money module or money service without holds) is charged here, on the
        /// caller's thread, as OpenSim always charged after an action.
        /// </summary>
        void Settle(bool succeeded);
    }

    /// <summary>GridGate: how OpenSim's own code takes a fee before an action, whatever the money module.</summary>
    public static class FeeHolds
    {
        private static readonly IFeeHold Nothing = new NothingHeld();

        /// <summary>
        /// The money module's own hold when it is an <see cref="IFeeHoldModule"/> (which decides the fee itself). Otherwise
        /// today's way: <c>UploadCovered</c> or <c>AmountCovered</c> now, and on <c>Settle(true)</c>
        /// <c>ApplyUploadCharge</c> or <c>ApplyCharge</c>. A hold that does nothing when there is no money module, or no
        /// fee for one without holds; null when the fee can't be paid.
        /// </summary>
        public static IFeeHold Take(IMoneyModule money, UUID agentID, int amount, MoneyTransactionType type, string extraData = "")
        {
            if (money == null)
                return Nothing;
            if (money is IFeeHoldModule holds)
                return holds.HoldFee(agentID, amount, type, extraData);
            if (amount <= 0)
                return Nothing;

            bool covered;
            if (type == MoneyTransactionType.UploadCharge)
                covered = money.UploadCovered(agentID, amount);
            else if (type == MoneyTransactionType.ClassifiedCharge && money is IClassifiedFeeModule fees)
                covered = fees.ClassifiedFeeCovered(agentID);
            else
                covered = money.AmountCovered(agentID, amount);
            return covered ? new ChargeAfter(money, agentID, amount, type, extraData) : null;
        }

        private sealed class NothingHeld : IFeeHold
        {
            public void Settle(bool succeeded)
            {
            }
        }

        /// <summary>Checked already; charged when the action worked, as OpenSim always did.</summary>
        private sealed class ChargeAfter : IFeeHold
        {
            private readonly IMoneyModule m_money;
            private readonly UUID m_agentID;
            private readonly int m_amount;
            private readonly MoneyTransactionType m_type;
            private readonly string m_extraData;
            private int m_settled;

            public ChargeAfter(IMoneyModule money, UUID agentID, int amount, MoneyTransactionType type, string extraData)
            {
                m_money = money;
                m_agentID = agentID;
                m_amount = amount;
                m_type = type;
                m_extraData = extraData;
            }

            public void Settle(bool succeeded)
            {
                if (Interlocked.Exchange(ref m_settled, 1) != 0 || !succeeded)
                    return;
                if (m_type == MoneyTransactionType.UploadCharge)
                    m_money.ApplyUploadCharge(m_agentID, m_amount, "Asset upload");
                else
                    m_money.ApplyCharge(m_agentID, m_amount, m_type, m_extraData);
            }
        }
    }
}
