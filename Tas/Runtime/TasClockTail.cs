using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Exists only so the tick's tail runs at the END of the FixedUpdate batch.
    ///
    /// If the clock fired head+tail from its own -32000 FixedUpdate, capture would run before
    /// every other script's FixedUpdate - i.e. before the sim that consumed the input. With the
    /// split, a tick is: [head -32000] -> all gameplay FixedUpdate -> [tail +32000]. Note that
    /// in Mode A the physics integration for this tick still lands after the tail (Unity steps
    /// physics after the FixedUpdate phase), which is a CONSTANT one-tick input-to-state offset.
    /// It cancels out between record and playback, so replay stays exact; if you want zero
    /// offset between input and state in the same tick, run Mode B (Physics.Simulate inside Step).
    /// </summary>
    [DefaultExecutionOrder(32000)]
    sealed class TasClockTail : MonoBehaviour
    {
        void Awake()
        {
            if (TasClock.i == null) { enabled = false; return; }
        }

        void FixedUpdate()
        {
            TasClock c = TasClock.i;
            if (c == null) return;
            if (c.mode == TasClockMode.Manual) return;   // Step() calls Tail() itself
            if (!c.tailDue) return;                      // held/paused frame: no tick, so no tail
            c.tailDue = false;
            c.Tail();
        }
    }
}
