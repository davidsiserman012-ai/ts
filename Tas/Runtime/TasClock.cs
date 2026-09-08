using System;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// The one authoritative clock. Everything in the simulation reads TasClock.Delta /
    /// TasClock.Tick instead of Time.deltaTime / Time.frameCount.
    ///
    /// This is what fixes the core flaw in the old DemoRecorder, which sampled with
    ///     fpsTimeTotal += Time.deltaTime;  if (fpsTimeTotal < 1f/fpsCap) return;
    /// That is a *variable-rate* sampler: on a 30fps device it emits 1 sample per render
    /// frame, so demoData.frames.Count stops meaning "tick" and the frame-index-as-time
    /// trick silently desyncs from wall clock time. Here the tick counter is the clock.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public sealed class TasClock : MonoBehaviour
    {
        public static TasClock i;

        [Header("Authoritative sim rate")]
        [Tooltip("Ticks per second. Recorded into the tape header; playback must use the recorded value.")]
        public int tickRate = 60;

        [Header("Modes")]
        [Tooltip("false = Mode A (world runs on its own FixedUpdate, we only swap the input source).\n" +
                 "true  = Mode B (tool mode: we freeze the world and step it ourselves).")]
        public bool manual;

        public bool paused;
        public double speed = 1.0;          // playback rate multiplier (Mode B only)

        public long tick;                    // monotonic, never reset mid-run
        public double simTime;               // seconds == tick * (1/tickRate), exact

        public event Action OnTickHead;      // flush deferred events, then apply/sample input
        public event Action OnTickTail;      // capture input frame, checksum, stream to disk
        public event Action<float> OnSimTick;// sim bus, Mode B only

        public static float TickDelta { get; private set; }
        public static bool Exists => i != null;

        /// <summary>THE patch rule: sim code uses this, never Time.deltaTime.</summary>
        public static float Delta
        {
            // If no clock is in the scene yet, fall back to the engine's own step so that
            // forgetting TasClock degrades to "today's behaviour" instead of a divide-by-zero sim.
            get { return TickDelta > 0f ? TickDelta : UnityEngine.Time.deltaTime; }
        }

        /// <summary>Sim time in seconds. Never Time.time / Time.unscaledTime in sim code.</summary>
        public static float Time_s => i != null ? (float)i.simTime : UnityEngine.Time.unscaledTime;

        public static long Tick => i != null ? i.tick : -1;
        public static bool Manual => i != null && i.manual;

        public static float Rate => i != null ? i.tickRate : 60f;

        private int pendingSteps;
        private double accum;

        private void Awake()
        {
            i = this;
            SetTickRate(tickRate);
            if (GetComponent<TasClockTail>() == null)
            {
                tailGo = new GameObject("TasClockTail");
                tailGo.transform.SetParent(transform, false);
                tailGo.AddComponent<TasClockTail>();
            }
        }

        private void OnDisable() { if (i == this) i = null; }

        /// <summary>Tail driver, created at runtime so its DefaultExecutionOrder applies.</summary>
        GameObject tailGo;

        public void SetTickRate(int hz)
        {
            tickRate = Mathf.Max(1, hz);
            TickDelta = 1f / tickRate;

            // Mode A only: pin the engine's fixed step to the tape rate so the world advances
            // exactly one tick per TAS tick. Physics.fixedDeltaTime (2022.2+) is separate from
            // Time.fixedDeltaTime, so set whichever the project has.
            UnityEngine.Time.fixedDeltaTime = TickDelta;
#if UNITY_2022_2_OR_NEWER
            Physics.fixedDeltaTime = TickDelta;
#endif
            // Allow up to 6 catch-up substeps instead of the default clamp; a dropped render
            // frame must never turn into a dropped *simulation* tick.
            UnityEngine.Time.maximumDeltaTime = TickDelta * 6f;
        }

        public void SetManual(bool on)
        {
            manual = on;
            TasPhysics.SetManual(on);
            UnityEngine.Time.timeScale = on ? 0f : 1f;
            accum = 0.0;
        }

        public void ResetClock()
        {
            tick = 0;
            simTime = 0.0;
            accum = 0.0;
            pendingSteps = 0;
        }

        public void StepOnce() { pendingSteps++; }

        private void FixedUpdate()
        {
            if (manual) return;          // Mode B steps from the tool window
            if (paused) return;
            Head();                      // the tail is run by TasClockTail (+32000) at the end of the batch
        }

        /// <summary>Tick head in auto mode: clock advances, events drain, input is committed.</summary>
        void Head()
        {
            tick++;
            simTime += TickDelta;
            TasDeferred.DrainUpTo(tick);
            try { OnTickHead.Invoke(); } catch (Exception e) { Debug.LogError("TasClock OnTickHead: " + e); }
            TasInput.Commit();
        }

        /// <summary>Tick tail in auto mode: capture/checksum, then latch edges.</summary>
        public void Tail()
        {
            try { OnTickTail.Invoke(); } catch (Exception e) { Debug.LogError("TasClock OnTickTail: " + e); }
            TasInput.Latch();
        }

        private void Update()
        {
            if (!manual) return;

            if (paused)
            {
                if (pendingSteps > 0) { int n = pendingSteps; pendingSteps = 0; for (int k = 0; k < n; k++) Step(); }
                return;
            }

            pendingSteps = 0;
            accum += UnityEngine.Time.unscaledDeltaTime * speed;
            int budget = (int)accum;
            if (budget <= 0) return;
            budget = Mathf.Min(budget, 8);          // never simulate more than 8 ticks per render frame
            accum -= budget;
            for (int k = 0; k < budget; k++) Step();
        }

        /// <summary>
        /// One tick, identical ordering in record and playback:
        /// events -> input -> sim -> capture. Divergence between the two is a bug in
        /// this ordering, not in float luck.
        /// </summary>
        public void Step()
        {
            tick++;
            simTime += TickDelta;

            TasDeferred.DrainUpTo(tick);

            try { OnTickHead.Invoke(); }        // playback pushes this tick's tape frame here
            catch (Exception e) { Debug.LogError("TasClock OnTickHead: " + e); }

            TasInput.Commit();                  // live read or tape -> TasInput.Current

            if (manual)
            {
                try { OnSimTick.Invoke(TickDelta); }
                catch (Exception e) { Debug.LogError("TasClock OnSimTick: " + e); }
                TasPhysics.Step(TickDelta);
            }

            try { OnTickTail.Invoke(); }         // recorder captures + checksums here
            catch (Exception e) { Debug.LogError("TasClock OnTickTail: " + e); }

            TasInput.Latch();
        }

        public static double TicksToSeconds(double t) { return t * TickDelta; }
        public static int SecondsToTicks(double s) { return Mathf.CeilToInt((float)(s * Rate)); }
    }

    /// <summary>Base for anything that must be deterministic. Implement in Tick, not Update.</summary>
    public abstract class TasSimBehaviour : MonoBehaviour
    {
        protected abstract void Tick(float dt);

        private void FixedUpdate()
        {
            // Mode A: engine drives us. Mode B: the clock bus drives us. Never both.
            if (TasClock.Manual) return;
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
