using System;
using UnityEngine;

namespace Tas
{
    public enum TasClockMode
    {
        /// <summary>
        /// The engine drives everything; we only pin the cadence and swap the input source.
        /// With enforceFixedRate, Time.captureFramerate = tickRate, which makes
        /// Time.deltaTime EXACTLY 1/tickRate on every frame - so scripts you have not patched
        /// (your RigidbodyFirstPersonController reads Time.deltaTime) are still frame-exact
        /// and reproducible. This is the normal mode for playing AND recording AND 1x replay.
        /// </summary>
        Auto,

        /// <summary>
        /// We decide, once per render frame, whether a tick happens. Frames that do not tick get
        /// their simulation cancelled by restoring the last savestate in LateUpdate - before
        /// anything is drawn. That single mechanism is slow-mo (0.25x = tick every 4th frame),
        /// pause (never tick) and frame-advance (tick on demand), with no timeScale games and no
        /// patches to gameplay code. Requires the per-tick savestate ring.
        /// </summary>
        Hold,

        /// <summary>
        /// We own the step: Time.timeScale = 0, physics Manual, sim advanced by the tick bus
        /// (TasSimBehaviour or TasTickDriver). Needed for >1x turbo and for headless verification.
        /// Only correct if sim scripts read TasClock.Delta instead of Time.deltaTime - with
        /// timeScale 0 their own deltaTime is 0, so unpatched scripts freeze instead of stepping.
        /// </summary>
        Manual,
    }

    /// <summary>
    /// The one authoritative clock. Sim time is `tick * (1/tickRate)`, never an accumulator.
    ///
    /// This is what fixes the core flaw in the old DemoRecorder sampler
    /// (fpsTimeTotal += Time.deltaTime; if (fpsTimeTotal &lt; 1f/fpsCap) return;), which is
    /// variable-rate: on a 30fps device it emits one sample per render frame, so frames.Count
    /// stops being a time and playback drifts.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public sealed class TasClock : MonoBehaviour
    {
        public static TasClock i;

        [Header("Authoritative sim rate")]
        public int tickRate = 60;

        [Header("Modes")]
        public TasClockMode mode = TasClockMode.Auto;

        [Tooltip("Force 1 tick = 1 render frame while recording/playing. Keep on unless every sim " +
                 "script already reads TasClock.Delta.")]
        public bool enforceFixedRate = true;

        public bool paused;
        public double speed = 1.0;              // Hold: ticks per render frame. Manual: ticks per second-ish.

        public long tick;                        // monotonic; the tape index
        public double simTime;                   // == tick * (1/tickRate)

        public event Action OnTickHead;          // playback pushes the tape frame here
        public event Action OnTickTail;          // clock-level bookkeeping (input latching)
        public event Action OnTickCapture;       // recorder + playback sample state HERE, see below
        public event Action<float> OnSimTick;     // sim bus, Manual only

        public static float TickDelta { get; private set; }
        public static bool Exists => i != null;
        public bool manual { get { return mode == TasClockMode.Manual; } }

        /// <summary>True when this frame's tail callback should run (set by Head, cleared on holds).</summary>
        public bool tailDue;

        /// <summary>
        /// True for the whole frame in which the sim was cancelled (slow-mo hold / pause). One-way
        /// door: any *timer-like* thing that runs on Unity's clock - Invoke, coroutines, a UI
        /// countdown, your TimeManager - keeps advancing during held frames because we cannot reach
        /// it. Guard those with this flag (one line each) or run slow-mo at 1x. GameManager has both
        /// Invoke and a WaitForFixedUpdate coroutine, so this is not hypothetical for you.
        /// </summary>
        public static bool CancelledFrame { get; private set; }

        /// <summary>Frames that took >3x the tick to render while the sim was unpinned.</summary>
        public int slowFrames;

        /// <summary>THE patch rule for anyone who wants turbo/Manual: sim code uses this, not Time.deltaTime.</summary>
        public static float Delta
        {
            get { return TickDelta > 0f ? TickDelta : UnityEngine.Time.deltaTime; }
        }

        public static float Time_s { get { return i != null ? (float)i.simTime : UnityEngine.Time.unscaledTime; } }
        public static long Tick { get { return i != null ? i.tick : -1; } }
        public static bool Manual { get { return i != null && i.mode == TasClockMode.Manual; } }
        public static bool Hold { get { return i != null && i.mode == TasClockMode.Hold; } }
        public static float Rate { get { return i != null ? i.tickRate : 60f; } }

        double holdAccum;
        int pendingSteps;
        bool cancelThisFrame;

        void Awake()
        {
            i = this;
            SetTickRate(tickRate);
            if (GetComponent<TasClockTail>() == null)
            {
                GameObject go = new GameObject("TasClockTail");
                go.transform.SetParent(transform, false);
                go.AddComponent<TasClockTail>();
            }
        }

        void OnDisable()
        {
            UnityEngine.Time.captureFramerate = 0;
            if (i == this) i = null;
        }

        public void SetTickRate(int hz)
        {
            tickRate = Mathf.Clamp(hz, 10, 1000);
            TickDelta = 1f / tickRate;
            holdAccum = 1.0;                      // tick immediately on the first frame

            UnityEngine.Time.fixedDeltaTime = TickDelta;
#if UNITY_2022_2_OR_NEWER
            Physics.fixedDeltaTime = TickDelta;
#endif
            UnityEngine.Time.maximumDeltaTime = TickDelta * 6f;

            if (mode != TasClockMode.Manual && enforceFixedRate)
                UnityEngine.Time.captureFramerate = tickRate;
        }

        public void ResetClock()
        {
            tick = 0;
            simTime = 0.0;
            holdAccum = 1.0;
            pendingSteps = 0;
            TasSim.Accumulator = 2166136261u;
        }

        public void SetManual(bool on) { SetMode(on ? TasClockMode.Manual : TasClockMode.Auto); }

        public void SetMode(TasClockMode m)
        {
            if (m == mode) { ApplyMode(); return; }
            mode = m;
            cancelThisFrame = false;
            ApplyMode();
        }

        void ApplyMode()
        {
            switch (mode)
            {
                case TasClockMode.Manual:
                    TasPhysics.SetManual(true);
                    UnityEngine.Time.timeScale = 0f;
                    UnityEngine.Time.captureFramerate = 0;
                    break;
                case TasClockMode.Hold:
                    TasPhysics.SetManual(false);
                    UnityEngine.Time.timeScale = 1f;
                    UnityEngine.Time.captureFramerate = enforceFixedRate ? tickRate : 0;
                    holdAccum = 1.0;
                    break;
                default:
                    TasPhysics.SetManual(false);
                    UnityEngine.Time.timeScale = 1f;
                    UnityEngine.Time.captureFramerate = enforceFixedRate ? tickRate : 0;
                    break;
            }
        }

        public void ReleaseRateLock()
        {
            UnityEngine.Time.captureFramerate = 0;
        }

        /// <summary>Queue exact ticks to run even while paused (frame advance).</summary>
        public void StepOnce() { pendingSteps++; }

        void FixedUpdate()
        {
            tailDue = false;
            if (mode == TasClockMode.Manual) return;         // Step() drives itself there

            if (paused && pendingSteps <= 0)
            {
                if (mode == TasClockMode.Hold) cancelThisFrame = true;
                return;
            }

            if (!enforceFixedRate && UnityEngine.Time.unscaledDeltaTime > TickDelta * 3f) slowFrames++;

            if (pendingSteps > 0)
            {
                int n = pendingSteps;
                pendingSteps = 0;
                for (int k = 0; k < n; k++) Head();
                return;
            }

            if (mode == TasClockMode.Hold)
            {
                holdAccum += speed;
                int budget = (int)holdAccum;
                if (budget <= 0)
                {
                    cancelThisFrame = true;                   // slow-mo / pause: undo this frame's sim
                    return;
                }
                holdAccum -= budget;
                budget = Mathf.Min(budget, 2);
                for (int k = 0; k < budget; k++) Head();
                return;
            }

            Head();
        }

        /// <summary>
        /// In Hold mode, frames that must not advance still let the engine simulate. Undoing that
        /// in LateUpdate - i.e. after all FixedUpdate/Update, before rendering - is what makes the
        /// held frame invisible. The state we restore is the one the savestate ring already holds.
        /// </summary>
        void LateUpdate()
        {
            CancelledFrame = cancelThisFrame;
            if (!cancelThisFrame) return;
            cancelThisFrame = false;
            if (mode != TasClockMode.Hold) return;
            TasSavestates.RestoreNewest();
            CancelledFrame = false;
        }

        /// <summary>
        /// CAPTURE POINT. Your game writes input in InputManager.Update() and consumes it in
        /// RigidbodyFirstPersonController.Update() - inside the SAME Update batch, with no tick
        /// boundary between them. So sampling at the tick tail (a FixedUpdate callback, which runs
        /// BEFORE that Update batch) reads last frame's value: the tape ends up shifted one tick
        /// against the physics, i.e. every replayed input is applied one frame later than it was
        /// during recording. Constant, so it never explodes - it just quietly never matches, and a
        /// 1-tick error on a fast strafe is already past any correction tolerance.
        /// Capture therefore happens in LateUpdate of the frame whose tick it is: after the sim
        /// consumed the input, before the frame is drawn.
        /// </summary>
        internal int capturePending;

        internal void RunCaptures()
        {
            while (capturePending > 0)
            {
                capturePending--;
                try { OnTickCapture.Invoke(); }
                catch (Exception e) { Debug.LogError("TasClock OnTickCapture: " + e); }
            }
        }

        void Head()
        {
            tick++;
            simTime += TickDelta;

            TasDeferred.DrainUpTo(tick);

            try { OnTickHead.Invoke(); }
            catch (Exception e) { Debug.LogError("TasClock OnTickHead: " + e); }

            TasInput.Commit();

            tailDue = true;
            if (!manual) capturePending++;      // drained in LateUpdate, after the sim consumed input
        }

        public void Tail()
        {
            try { OnTickTail.Invoke(); }
            catch (Exception e) { Debug.LogError("TasClock OnTickTail: " + e); }
            TasInput.Latch();
        }

        public void Capture()
        {
            try { OnTickCapture.Invoke(); }
            catch (Exception e) { Debug.LogError("TasClock OnTickCapture: " + e); }
        }

        /// <summary>One full tick in Manual mode: head, bus, physics, tail.</summary>
        public void Step()
        {
            Head();

            try { OnSimTick.Invoke(TickDelta); }
            catch (Exception e) { Debug.LogError("TasClock OnSimTick: " + e); }
            TasPhysics.Step(TickDelta);

            Tail();
            Capture();          // Manual mode has no Update batch, so the tail IS the capture point
        }

        public static double TicksToSeconds(double t) { return t * TickDelta; }
        public static int SecondsToTicks(double s) { return Mathf.CeilToInt((float)(s * Rate)); }
    }

    /// <summary>Base for anything that must be deterministic. Implement in Tick, not Update.</summary>
    public abstract class TasSimBehaviour : MonoBehaviour
    {
        protected abstract void Tick(float dt);

        void FixedUpdate()
        {
            if (TasClock.Manual) return;      // Mode Manual drives us through the bus; never both
            Tick(TasClock.Delta);
        }

        protected virtual void OnEnable()
        {
            if (TasClock.Exists) TasClock.i.OnSimTick += Tick;
        }

        protected virtual void OnDisable()
        {
            if (TasClock.Exists) TasClock.i.OnSimTick -= Tick;
        }
    }
}
