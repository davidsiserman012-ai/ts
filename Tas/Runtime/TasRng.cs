using System;

namespace Tas
{
    /// <summary>
    /// Owned, serializable RNG. UnityEngine.Random and System.Random are global /
    /// unseedable state that no replay can restore, so any gameplay use of them is a
    /// desync waiting to happen. xorshift64* is 4 ops, identical on every IL2CPP target,
    /// and its whole state is one ulong - which is why it can live in the tape header.
    /// </summary>
    public struct TasRng
    {
        public ulong s;

        public TasRng(ulong seed) { s = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed; }

        public ulong NextU64()
        {
            s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
            return s * 2685821657736338717UL;
        }

        public uint NextU32() { return (uint)(NextU64() >> 32); }

        public float Next01() { return (NextU64() >> 40) * (1.0f / 16777216.0f); }

        public int Range(int minIncl, int maxExcl)
        {
            if (maxExcl <= minIncl) return minIncl;
            return minIncl + (int)(NextU32() % (uint)(maxExcl - minIncl));
        }

        public static TasRng FromNow() { return new TasRng((ulong)DateTime.UtcNow.Ticks); }

        /// <summary>Rolling hash of (tick, rng state, key sim floats) for divergence checks.</summary>
        public static uint Mix(uint h, float v)
        {
            unchecked
            {
                uint b = BitConverter.ToUInt32(BitConverter.GetBytes(v), 0);
                h ^= b;
                h *= 16777619u;
                return h;
            }
        }

        public static uint Mix(uint h, int v)
        {
            unchecked
            {
                h ^= (uint)v;
                h *= 16777619u;
                return h;
            }
        }
    }
}
