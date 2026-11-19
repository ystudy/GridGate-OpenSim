using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Framework
{
    /// <summary>
    /// GridGate: the price this program last sent to each viewer for a group's membership or a parcel's pass, kept per
    /// person and group or parcel for <see cref="Lifetime"/>. A direct group join and a pass purchase carry no price, so
    /// the viewer's dialog is the only place the person saw one: the click charges at most the price sent. When the price is
    /// higher now, or none was sent, the click is refused, the current price is remembered (the next click goes through at
    /// it), and the caller tells the person. A price that went down charges the lower one, and free stays free. When full,
    /// the oldest entry goes first. Safe to use from many threads.
    /// </summary>
    public sealed class ShownPrices
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        private readonly Func<DateTime> m_utcNow;
        private readonly int m_capacity;
        private readonly object m_lock = new();
        private readonly Dictionary<(UUID Agent, UUID Thing), LinkedListNode<(UUID Agent, UUID Thing, int Price, DateTime At)>> m_prices = new();
        private readonly LinkedList<(UUID Agent, UUID Thing, int Price, DateTime At)> m_order = new(); // oldest first

        public ShownPrices(Func<DateTime> utcNow = null, int capacity = 10000)
        {
            m_utcNow = utcNow ?? (() => DateTime.UtcNow);
            m_capacity = Math.Max(1, capacity);
        }

        /// <summary>Remembers that <paramref name="agent"/> was shown <paramref name="price"/> for <paramref name="thing"/>.</summary>
        public void Remember(UUID agent, UUID thing, int price)
        {
            lock (m_lock)
                Put(agent, thing, price);
        }

        /// <summary>
        /// Whether <paramref name="agent"/>'s click may charge <paramref name="current"/> for <paramref name="thing"/>: it may
        /// when that is free, or no more than the price they were last shown within <see cref="Lifetime"/> (the lower price is
        /// remembered then). Otherwise it is refused, and <paramref name="current"/> is remembered as the price shown now.
        /// </summary>
        public bool MayCharge(UUID agent, UUID thing, int current)
        {
            if (current <= 0)
                return true;

            lock (m_lock)
            {
                if (m_prices.TryGetValue((agent, thing), out var node))
                {
                    if (m_utcNow() - node.Value.At > Lifetime)
                        Drop(agent, thing);
                    else if (current <= node.Value.Price)
                    {
                        Put(agent, thing, current);
                        return true;
                    }
                }
                Put(agent, thing, current);
                return false;
            }
        }

        private void Put(UUID agent, UUID thing, int price)
        {
            Drop(agent, thing);
            while (m_prices.Count >= m_capacity)
                Drop(m_order.First.Value.Agent, m_order.First.Value.Thing);
            m_prices[(agent, thing)] = m_order.AddLast((agent, thing, price, m_utcNow()));
        }

        private void Drop(UUID agent, UUID thing)
        {
            if (m_prices.Remove((agent, thing), out var node))
                m_order.Remove(node);
        }
    }
}
