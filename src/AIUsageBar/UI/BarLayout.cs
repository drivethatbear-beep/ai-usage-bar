using System;

namespace AIUsageBar.UI
{
    internal struct BarFit : IEquatable<BarFit>
    {
        public readonly int Level;
        public readonly bool Reset;
        public readonly bool Names;

        public BarFit(int level, bool reset, bool names)
        {
            Level = level;
            Reset = reset;
            Names = names;
        }

        public bool Equals(BarFit o) => Level == o.Level && Reset == o.Reset && Names == o.Names;
        public override bool Equals(object obj) => obj is BarFit f && Equals(f);
        public override int GetHashCode() => (Level * 4) + (Reset ? 2 : 0) + (Names ? 1 : 0);
        public override string ToString() => $"L{Level} reset={Reset} names={Names}";
    }

    internal static class BarLayout
    {
        /// <summary>
        /// Picks the layout that fits <paramref name="maxWidth"/> at the largest size: at each size level from
        /// <paramref name="baseLevel"/> down to <paramref name="minLevel"/> (100 %) it tries everything, then without
        /// service names, then without the reset column; only when nothing fits at 100 % does it go smaller.
        /// Readable text wins over extras (the card shows names and resets anyway).
        /// </summary>
        public static BarFit Fit(int baseLevel, bool wantReset, Func<int, bool, bool, int> widthOf, int maxWidth, int minLevel)
        {
            var floor = Math.Min(baseLevel, minLevel);
            var options = wantReset
                ? new[] { (true, true), (true, false), (false, false) }
                : new[] { (false, true), (false, false) };
            for (var level = baseLevel; level >= floor; level--)
                foreach (var (reset, names) in options)
                    if (widthOf(level, reset, names) <= maxWidth) return new BarFit(level, reset, names);
            for (var level = floor - 1; level >= 1; level--)
                if (widthOf(level, false, false) <= maxWidth) return new BarFit(level, false, false);
            return new BarFit(1, false, false);
        }
    }
}
