/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.ScriptEngine.Shared.Api.Plugins
{
    // GridGate: script payouts (llGiveMoney, llTransferLindenDollars) run here, not on the dataserver work pool, so a slow
    // money service can hold up payouts but never notecard reads and other dataserver requests. One queue for the whole
    // process, two threads: a payout already queued still runs if its script is removed, as a payment sent before the removal.
    public class Payouts
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static readonly Payouts Instance = new Payouts();

        private readonly ObjectJobEngine m_WorkPool;

        private Payouts()
        {
            m_WorkPool = new ObjectJobEngine(ProcessAction, "ScriptPayouts", 1000, 2);
        }

        // GridGate: the most payouts one owner can have waiting (queued or running) at once.
        public const int MaxWaitingPerOwner = 60;

        private readonly Dictionary<UUID, int> m_Waiting = new();

        /// <summary>
        /// Queues a payout for <paramref name="owner"/>, or returns false when that owner already has
        /// <see cref="MaxWaitingPerOwner"/> waiting. The place is given back when the payout has run, or been dropped.
        /// </summary>
        public bool TryEnqueue(UUID owner, Action<string> action)
        {
            lock (m_Waiting)
            {
                m_Waiting.TryGetValue(owner, out int n);
                if (n >= MaxWaitingPerOwner)
                    return false;
                m_Waiting[owner] = n + 1;
            }

            void run(string s)
            {
                try
                {
                    action(s);
                }
                finally
                {
                    Release(owner);
                }
            }

            try
            {
                m_WorkPool.Enqueue((Action<string>)run);
            }
            catch
            {
                Release(owner);
                throw;
            }
            return true;
        }

        private void Release(UUID owner)
        {
            lock (m_Waiting)
            {
                if (!m_Waiting.TryGetValue(owner, out int n))
                    return;
                if (n <= 1)
                    m_Waiting.Remove(owner);
                else
                    m_Waiting[owner] = n - 1;
            }
        }

        private static void ProcessAction(object o)
        {
            if (o is not Action<string> action)
                return;
            try
            {
                action(string.Empty);
            }
            catch (Exception e)
            {
                m_log.Error("[SCRIPT PAYOUTS]: A script payout failed: " + e);
            }
        }
    }
}
