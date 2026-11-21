using System;
using System.Collections.Generic;

namespace GridGate.Region.Traffic
{
    /// <summary>
    /// Each parcel's recent visits (spec 2026-11-21-featured-places-design, "Visits"): an avatar counts once per parcel per
    /// UTC day, and the number fades by <c>exp(-elapsed / meanLife)</c>, so a steady N visits a day settles near N times the
    /// mean life in days. Not thread-safe: the module locks around it.
    /// </summary>
    /// <remarks>
    /// A parcel's number starts from its <c>seed</c> (the stored value) the first time it is touched. Who counted today is
    /// kept in memory only, so a region restart may count someone a second time that day.
    /// </remarks>
    internal sealed class ParcelTraffic
    {
        private readonly TimeSpan _meanLife;
        private readonly Dictionary<Guid, (double Value, DateTimeOffset Since)> _parcels = new();
        private readonly HashSet<(Guid Parcel, Guid Agent)> _today = new();
        private DateTime _day;

        public ParcelTraffic(TimeSpan meanLife) => _meanLife = meanLife;

        /// <summary><paramref name="value"/> as it stands at <paramref name="now"/>, having faded since <paramref name="since"/>.</summary>
        public static double Decayed(double value, DateTimeOffset since, DateTimeOffset now, TimeSpan meanLife) =>
            now <= since ? value : value * Math.Exp(-(now - since).TotalSeconds / meanLife.TotalSeconds);

        /// <summary>The parcel's number at <paramref name="now"/>, without changing it.</summary>
        public double ValueOf(Guid parcel, DateTimeOffset now, double seed)
        {
            var (value, since) = Get(parcel, now, seed);
            return Decayed(value, since, now, _meanLife);
        }

        /// <summary>Counts <paramref name="agent"/> on <paramref name="parcel"/> unless it already counted there this UTC day.</summary>
        public bool Visit(Guid parcel, Guid agent, DateTimeOffset now, double seed)
        {
            var day = now.UtcDateTime.Date;
            if (day != _day)
            {
                _today.Clear();
                _day = day;
            }
            if (!_today.Add((parcel, agent))) return false;
            _parcels[parcel] = (ValueOf(parcel, now, seed) + 1, now);
            return true;
        }

        /// <summary>Fades the parcel's number up to <paramref name="now"/> and keeps it from there; returns it.</summary>
        public double Settle(Guid parcel, DateTimeOffset now, double seed)
        {
            var value = ValueOf(parcel, now, seed);
            _parcels[parcel] = (value, now);
            return value;
        }

        private (double Value, DateTimeOffset Since) Get(Guid parcel, DateTimeOffset now, double seed) =>
            _parcels.TryGetValue(parcel, out var kept) ? kept : (seed, now);
    }
}
