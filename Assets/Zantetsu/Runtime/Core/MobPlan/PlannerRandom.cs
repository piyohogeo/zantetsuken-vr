using System;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// PROP-OBSTACLE-1: the simulation's random numbers with a state that can be read and set (xorshift64*), so a saved section
    /// of play can be taken up again from its start. A System.Random by type, for the callers that take one; every draw goes
    /// through this class's own state (System.Random's own is not used).
    /// </summary>
    public sealed class PlannerRandom : Random
    {
        private ulong state;

        public PlannerRandom(ulong seed) : base(0)
        {
            State = seed;
        }

        /// <summary>The whole state: setting it makes the draws that follow the same as after the moment it was read.</summary>
        public ulong State
        {
            get => state;
            set => state = value != 0UL ? value : 0x9E3779B97F4A7C15UL;   // the generator has no zero state
        }

        private ulong NextBits()
        {
            var x = state;
            x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
            state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        protected override double Sample() => (NextBits() >> 11) * (1.0 / 9007199254740992.0);   // 53 bits: [0, 1)
        public override double NextDouble() => Sample();
        public override int Next() => (int)(NextBits() >> 33);   // 31 bits: [0, int.MaxValue]
        public override int Next(int maxValue)
        {
            if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
            return (int)(Sample() * maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
            return minValue + (int)(Sample() * ((long)maxValue - minValue));
        }

        public override void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(NextBits() >> 56);
        }
    }
}
