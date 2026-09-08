using System.Collections.Generic;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Anything that arrives off-tick (network callbacks, UI toggles, ads, focus changes)
    /// must be stamped with the tick it *should* have applied on and drained at tick head.
    ///
    /// This exists because of your own On_Meta2Set: a Network callback that flips
    /// meta2Enabled / respawns the player / hands out a weapon lands between physics steps,
    /// and the tick it lands on depends on packet timing. Two runs of the same input tape
    /// then legitimately diverge. Queue + drain at head makes it part of the tape.
    /// </summary>
    public static class TasDeferred
    {
        struct E { public long tick; public Action act; public string tag; }

        static readonly List<E> queue = new List<E>(32);
        static readonly object gate = new object();

        public static void Post(string tag, Action act)
        {
            long t = TasClock.Tick;
            lock (gate) queue.Add(new E { tick = t, act = act, tag = tag });
        }

        /// <summary>Called from TasClock at OnTickHead, before input is sampled.</summary>
        public static void DrainUpTo(long tick)
        {
            if (queue.Count == 0) return;
            for (int k = 0; k < queue.Count; k++)
            {
                if (queue[k].tick > tick) continue;
                E e = queue[k];
                queue.RemoveAt(k--);
                try { e.act(); }
                catch (System.Exception ex) { Debug.LogError("TasDeferred " + e.tag + ": " + ex); }
            }
        }

        public static int Pending { get { lock (gate) return queue.Count; } }
        public static void Clear() { lock (gate) queue.Clear(); }
    }
}
