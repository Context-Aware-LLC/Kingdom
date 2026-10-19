using System;

namespace Kingdom.Presentation
{
    /// <summary>Local toy pacing. No match state, commands, or deterministic game RNG.</summary>
    internal sealed class TablePropRules
    {
        private double _lastCrownTap = double.NegativeInfinity;
        private double _nextCrownWarning = double.NegativeInfinity;
        private int _crownTaps;

        public bool TapCrown(double now)
        {
            if (now < _nextCrownWarning) return false;
            if (now - _lastCrownTap > 4d) _crownTaps = 0;
            _lastCrownTap = now;
            if (++_crownTaps < 3) return false;
            _crownTaps = 0;
            _nextCrownWarning = now + 8d;
            return true;
        }

        public static bool ReturnsSuggestionReceipt(int drawFromTwenty) => drawFromTwenty == 0;

        // Twenty physical orbs, twenty notes. A single glass strike is supplied at concert C;
        // independent sources retain their pitches when several orbs ring together.
        public static float OrbPitch(int index)
            => (float)Math.Pow(2d, (Math.Clamp(index, 0, 19) - 9d) / 12d);
    }
}
