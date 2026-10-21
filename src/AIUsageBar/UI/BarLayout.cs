using System;

namespace AIUsageBar.UI
{
    internal struct BarFit : IEquatable<BarFit>
    {
        public readonly int Level;
        public readonly bool Reset;
        public readonly bool Names;
        /// <summary>Every window's number; false shows only the tightest one per service.</summary>
        public readonly bool AllWindows;

        public BarFit(int level, bool reset, bool names, bool allWindows = true)
        {
            Level = level;
            Reset = reset;
            Names = names;
            AllWindows = allWindows;
        }

        public bool Equals(BarFit o) => Level == o.Level && Reset == o.Reset && Names == o.Names && AllWindows == o.AllWindows;
        public override bool Equals(object obj) => obj is BarFit f && Equals(f);
        public override int GetHashCode() => (Level * 8) + (AllWindows ? 4 : 0) + (Reset ? 2 : 0) + (Names ? 1 : 0);
        public override string ToString() => $"L{Level} reset={Reset} names={Names} all={AllWindows}";
    }

    internal static class BarLayout
    {
        /// <summary>
        /// Picks the layout that fits <paramref name="maxWidth"/> at the largest size: at each size level from
        /// <paramref name="baseLevel"/> down to <paramref name="minLevel"/> (100 %) it tries everything, then without
        /// service names, then without the reset column, then with one number per service (the tightest window,
        /// as the ring shows); only when nothing fits at 100 % does it go smaller. Readable text wins over extras
        /// (the card shows names, resets and every window anyway).
        /// </summary>
        public static BarFit Fit(int baseLevel, bool wantReset, Func<int, bool, bool, bool, int> widthOf, int maxWidth, int minLevel)
        {
            var floor = Math.Min(baseLevel, minLevel);
            var options = wantReset
                ? new[] { (true, true, true), (true, false, true), (false, false, true), (false, false, false) }
                : new[] { (false, true, true), (false, false, true), (false, false, false) };
            for (var level = baseLevel; level >= floor; level--)
                foreach (var (reset, names, all) in options)
                    if (widthOf(level, reset, names, all) <= maxWidth) return new BarFit(level, reset, names, all);
            for (var level = floor - 1; level >= 1; level--)
                if (widthOf(level, false, false, false) <= maxWidth) return new BarFit(level, false, false, false);
            return new BarFit(1, false, false, false);
        }
    }
}
