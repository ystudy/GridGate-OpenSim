using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: the join fee a group invitation showed, kept in memory by invite id for the groups modules whose service
    /// can't store it with the invitation (the XmlRpc groups module, Hypergrid groups, an older groups service), and for
    /// the fresh offers made when an accept finds the fee has gone up. An accept charges the current fee only while it
    /// is no more than the one offered here. An entry is forgotten after <see cref="Lifetime"/>; when full, the oldest
    /// goes first. Safe to use from many threads.
    /// </summary>
    public sealed class JoinFeeOffers
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

        private readonly Func<DateTime> m_utcNow;
        private readonly int m_capacity;
        private readonly object m_lock = new();
        private readonly Dictionary<UUID, LinkedListNode<(UUID Id, int Fee, DateTime At)>> m_offers = new();
        private readonly LinkedList<(UUID Id, int Fee, DateTime At)> m_order = new(); // oldest first

        public JoinFeeOffers(Func<DateTime> utcNow = null, int capacity = 10000)
        {
            m_utcNow = utcNow ?? (() => DateTime.UtcNow);
            m_capacity = Math.Max(1, capacity);
        }

        /// <summary>Remembers the fee offered with this invitation, replacing an earlier one.</summary>
        public void Remember(UUID inviteId, int fee)
        {
            lock (m_lock)
            {
                Drop(inviteId);
                while (m_offers.Count >= m_capacity)
                    Drop(m_order.First.Value.Id);
                m_offers[inviteId] = m_order.AddLast((inviteId, fee, m_utcNow()));
            }
        }

        /// <summary>The fee offered with this invitation; null when unknown or older than <see cref="Lifetime"/>.</summary>
        public int? Find(UUID inviteId)
        {
            lock (m_lock)
            {
                if (!m_offers.TryGetValue(inviteId, out var node))
                    return null;
                if (m_utcNow() - node.Value.At > Lifetime)
                {
                    Drop(inviteId);
                    return null;
                }
                return node.Value.Fee;
            }
        }

        public void Forget(UUID inviteId)
        {
            lock (m_lock)
                Drop(inviteId);
        }

        private void Drop(UUID inviteId)
        {
            if (m_offers.Remove(inviteId, out var node))
                m_order.Remove(node);
        }
    }
}
