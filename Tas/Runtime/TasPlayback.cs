using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tas
{
    public enum TasPlaybackMode
    {
        /// <summary>
        /// No re-simulation. Interpolate the tape's checkpoints and drive a camera rig.
        /// This is exactly what your leaderboard demo player does today, so it works on any
        /// device, any Unity patch, any CPU. Use it for the leaderboard. Zero cost.
        /// </summary>
        SpectateState,

        /// <summary>
        /// Re-simulate the real world from the input tape, correcting toward checkpoints when
        /// the sim and the tape disagree beyond tolerance. This is the TAS-tool view and the
        /// "run it myself" view.
        /// </summary>
        ResimLive,

        /// <summary>
        /// Re-simulate with NO correction and no rendering. Divergence is the verdict:
        /// clean => the run is legitimate for this configHash. Run this on the verify farm.
        /// </summary>
        ResimVerify,
    }

    [DefaultExecutionOrder(-31000)]   // right after the clock, before any gameplay FixedUpdate
    public sealed class TasPlayback : MonoBehaviour
    {
        public static TasPlayback i;

        public TasPlaybackMode mode = TasPlaybackMode.SpectateState;
        public float posTolerance = 0.35f;     // metres before a correction counts as divergence
        public bool blockInputWhilePlaying = true;

        public bool playing;
        public int index;
        public int length;
        public double speed = 1.0;
        public int divergences;
        public int corrections;
        public float lastError;

        TasTape tape;
        bool stepping;               // true while a re-sim loop owns index
        uint liveHash = 2166136261u;
        Dictionary<int, TasCheckpoint> byTick;
        public Transform rig;                  // what SpectateState moves
        public Camera rigCam;

        /// <summary>
        /// What the DEVICE is holding right now, during playback. If input reaches the sim from a path
        /// the tape never recorded (a direct Input.GetButton in the controller, a touch handler that
        /// writes physics state), the run still LOOKS like it replays fine and quietly isn't yours - so
        /// the tape is compared against a live read at every feed and a mismatch is reported on tick 40
        /// instead of on the leaderboard. Null means use TasLiveInput.PeekButtons(), which is pure
        /// (no cursor resync, no mouse-delta consumption). A game-side reader must be pure too, and must
        /// NOT be the frame sampler the capture uses: during replay that one returns the tape-fed struct,
        /// and comparing it to the tape is vacuously true - it would always pass.
        /// </summary>
        public static System.Func<uint> LiveButtons;

        /// <summary>The bits the check looks at, and only these (movement keys during a replay are legal).</summary>
        public static uint IntegrityMask = (uint)(TasButton.Jump | TasButton.Fire);

        /// <summary>false to stop probing the device during playback (one Input read per tick saved).</summary>
        public bool checkOffTapeInput = true;

        /// <summary>
        /// Game-side "is the cursor currently unlocked" probe, wired by TasUnityBridge. Needed because a
        /// frame recorded with the cursor free is a DIFFERENT frame than the same input with it locked
        /// (RotateView early-returns, so the turn is skipped while lookAccum still accrues), and the
        /// tool pins the cursor for you - so if the pin ever slips, the tape's per-frame
        /// TasFrameFlags.CursorUnlocked says what actually happened and this compares against it.
        /// </summary>
        public static System.Func<bool> CursorUnlockedProbe;
        public int cursorMismatchTicks;
        int cursorMismatchLogged;

        public int inputIntegrityErrors;
        int integrityLogged;

        public event Action OnFinished;
        public event Action<int> OnIndexChanged;

        void Awake() { i = this; }
        void OnDestroy() { if (i == this) i = null; }
        void OnDisable() { Detach(); }

        public void Load(TasTape t)
        {
            tape = t;
            length = t == null ? 0 : t.frames.Count;
            byTick = new Dictionary<int, TasCheckpoint>();
            if (t != null)
                for (int k = 0; k < t.checkpoints.Count; k++) byTick[t.checkpoints[k].tick] = t.checkpoints[k];
            index = 0;
        }

        public bool Play(TasTape t = null, int fromTick = 0, TasPlaybackMode m = TasPlaybackMode.ResimLive)
        {
            if (t != null) Load(t);
            if (tape == null || length == 0) { Debug.LogWarning("[TAS] no tape loaded"); return false; }
            if (!TasClock.Exists) { Debug.LogError("[TAS] TasClock is required for any playback, even SpectateState"); return false; }

            if (tape.header.tickRate != TasClock.i.tickRate)
                TasClock.i.SetTickRate(tape.header.tickRate);   // never trust the local default

            string why = CompatibleReason();
            if (why != null)
            {
                Debug.LogError("[TAS] refusing to play: " + why);
                return false;
            }

            mode = m;
            playing = true;
            index = Mathf.Clamp(fromTick, 0, length - 1);
            divergences = 0; corrections = 0; liveHash = 2166136261u;
            inputIntegrityErrors = 0; integrityLogged = 0;
            cursorMismatchTicks = 0; cursorMismatchLogged = 0;

            if (m == TasPlaybackMode.ResimVerify) TasClock.i.ResetClock();
            TasClock.i.paused = false;
            TasClock.i.speed = speed;
            ChooseMode();

            TasInput.SourcedFromTape = true;
            if (blockInputWhilePlaying) TasInput.BlockLiveInput = true;

            TasClock.i.OnTickHead += OnHead;
            TasClock.i.OnTickCapture += OnTail;
            enabled = true;

            if (mode == TasPlaybackMode.SpectateState) ShowStateAt(index);
            return true;
        }

        public void Stop()
        {
            playing = false;
            Detach();
            if (TasClock.Exists) { TasClock.i.SetMode(TasClockMode.Auto); TasClock.i.paused = false; }
            TasInput.ReleaseToLive();
            TasInput.BlockLiveInput = false;
        }

        /// <summary>
        /// Spectate needs nothing. 1x re-sim is just the game running normally with the tape
        /// feeding its input (Auto mode, engine cadence - smoothest and requires no patches).
        /// Below 1x or paused, Hold mode cancels the non-tick frames. Only headless verification
        /// uses Manual, where we drive the step ourselves and rendering is irrelevant.
        /// </summary>
        public void ChooseMode()
        {
            if (!TasClock.Exists) return;
            if (mode == TasPlaybackMode.SpectateState) { TasClock.i.SetMode(TasClockMode.Auto); return; }
            if (mode == TasPlaybackMode.ResimVerify) { TasClock.i.SetMode(TasClockMode.Manual); return; }
            TasClock.i.SetMode(Mathf.Abs((float)speed - 1f) < 0.001f && !TasClock.i.paused
                                ? TasClockMode.Auto : TasClockMode.Hold);
        }

        void Detach()
        {
            if (TasClock.Exists)
            {
                TasClock.i.OnTickHead -= OnHead;
                TasClock.i.OnTickCapture -= OnTail;
            }
        }

        /// <summary>
        /// A replay that starts mid-run needs the sim state at that tick. Two options, both
        /// implemented: keyframe snapshots when the tool recorded them, otherwise re-sim from 0.
        /// Re-sim from 0 at 8x-16x is normally faster than storing a snapshot every tick.
        /// </summary>
        public void SeekTo(int tick)
        {
            if (tape == null || !TasClock.Exists) return;
            tick = Mathf.Clamp(tick, 0, length - 1);
            if (mode == TasPlaybackMode.SpectateState) { index = tick; ShowStateAt(tick); return; }

            // Exact rewind if the per-tick ring still holds that tick (the usual case with
            // snapshotEveryTick on). Otherwise fall back to the sparse keyframes and re-sim the
            // difference, and if there is nothing to restore, re-sim from tick 0.
            TasStateEntry e;
            if (TasSavestates.TryRollback(tick, out e))
            {
                index = tick;
                if (OnIndexChanged != null) OnIndexChanged(index);
                return;
            }
            int key = tape.NearestCheckpointBefore(tick);
            int start = key >= 0 ? tape.checkpoints[key].tick : 0;
            TasSnapshot.RestoreIfAvailable(start);
            stepping = true;
            for (int k = start; k < tick; k++) { index = k; Feed(k); TasClock.i.Step(); }
            stepping = false;
            index = tick;
            if (OnIndexChanged != null) OnIndexChanged(index);
        }

        /// <summary>
        /// n &gt; 0 steps forward one tick at a time (cheap). n &lt; 0 goes back, which always means
        /// "restore nearest keyframe, re-sim forward" - the asymmetry every TAS tool has and
        /// the reason keyframe spacing is a space/time trade you should tune deliberately.
        /// </summary>
        public void StepForward(int n = 1)
        {
            if (!TasClock.Exists) return;
            if (TasClock.i.mode != TasClockMode.Hold) TasClock.i.SetMode(TasClockMode.Hold);
            TasClock.i.paused = true;
            if (n < 0) { SeekTo(Mathf.Max(0, index + n)); return; }
            stepping = true;
            for (int k = 0; k < n && index < length; k++) { Feed(index); TasClock.i.Step(); index++; }
            stepping = false;
            if (OnIndexChanged != null) OnIndexChanged(index);
        }

        void OnHead()
        {
            if (!playing || stepping) return;
            if (index >= length) { Finish(); return; }
            Feed(index);
        }

        void Feed(int i0)
        {
            TasInputFrame t = tape.frames[i0];
            TasInput.PushFromTape(t);
            bool pinLocked = false;

            // Does the DEVICE have something the tape does not? That is the shape of every "the replay
            // looked perfect but wasn't mine" bug: a direct Input.GetButton inside the sim, a touch
            // handler that writes physics state, a UI callback. Comparing look/aux here would fire on
            // every frame of a legitimate session (you are allowed to move the mouse while watching), so
            // it is held-button bits only, and only in the off-tape direction.
            if (checkOffTapeInput && !TasClock.CancelledFrame)
            {
                uint live = LiveButtons != null ? LiveButtons() : TasLiveInput.PeekButtons();
                // IntegrityMask is the force-applying buttons on purpose: watching a replay with your hand on
                // W is legal and harmless - the sim is being fed the tape - so flagging that would be
                // noise. A device-held JUMP/FIRE is different: those are exactly the bits that their
                // controller adds to jump force from a direct Input.GetButton, i.e. the one path where a
                // key on your keyboard really does change a "replayed" run.
                if ((pinLocked = ((t.flags & (byte)TasFrameFlags.CursorUnlocked) != 0)) !=
                    (CursorUnlockedProbe != null && CursorUnlockedProbe()))
                {
                    cursorMismatchTicks++;
                    if (cursorMismatchLogged < 3)
                    {
                        cursorMismatchLogged++;
                        Debug.LogWarning("[TAS] cursor lock state at tick " + i0 + " is " +
                            ((t.flags & (byte)TasFrameFlags.CursorUnlocked) != 0 ? "UNLOCKED in the tape, locked now"
                                                                                  : "locked in the tape, UNLOCKED now") +
                            ". Turning (and therefore the bhop turn-boost) is skipped while unlocked, so this " +
                            "replay cannot match the run - the tool pins the cursor while a session owns it, " +
                            "and something released or re-took it (Escape via InputManager.KeyboardSupport, a " +
                            "pause menu, or your own SetCursorState call).");
                    }
                }

                uint extra = live & IntegrityMask & ~t.buttons;
                if (extra != 0u)
                {
                    inputIntegrityErrors++;
                    if (integrityLogged < 8)
                    {
                        integrityLogged++;
                        Debug.LogWarning("[TAS] INPUT NOT IN TAPE at tick " + i0 + ": device holds " +
                                         TasButtonNames(extra) + " that frame does not. Something is reading " +
                                         "the keyboard inside the sim (see Docs/TasControllerAudit.md - the " +
                                         "Input.GetButton(\"Jump\") lines) - this replay is not the tape.");
                    }
                }
            }
        }

        static string TasButtonNames(uint bits)
        {
            string s = null;
            foreach (TasButton b in System.Enum.GetValues(typeof(TasButton)))
                if ((bits & (uint)b) != 0u) s = (s == null ? "" : s + "|") + b;
            return s != null ? s : "0x" + bits.ToString("X8");
        }

        void OnTail()
        {
            if (tape == null || length == 0 || index >= length) return;

            TasInputFrame f = tape.frames[index];
            liveHash = f.Fold(liveHash);                                  // the same one function
            Transform p = TasRecorder.i != null ? TasRecorder.i.player : null;
            uint simHash = TasInputFrame.StateHash(liveHash, p != null ? p.position : Vector3.zero);

            TasCheckpoint c;
            if (byTick != null && byTick.TryGetValue(index, out c))
            {
                float err = p != null ? Vector3.Distance(p.position, c.Position) : 0f;
                lastError = err;
                if (err > posTolerance || (c.stateHash != 0u && c.stateHash != simHash))
                {
                    divergences++;
                    if (mode == TasPlaybackMode.ResimVerify)
                    {
                        Debug.LogWarning("[TAS] DIVERGENCE tick=" + index + " err=" + err.ToString("0.000") +
                                         " hashTape=" + c.stateHash.ToString("X8") + " hashSim=" + simHash.ToString("X8"));
                    }
                    else if (mode == TasPlaybackMode.ResimLive && err > posTolerance)
                    {
                        corrections++;
                        TasInputFrame corr = f;
                        corr.flags |= (byte)TasFrameFlags.Resynced;
                        if (p != null) p.position = c.Position;     // snap, then keep simulating
                        TasRecorder rs = TasRecorder.i;
                        if (rs != null && rs.playerCam != null)
                            rs.playerCam.rotation = Quaternion.Euler(c.camPitch, c.bodyYaw, 0f);
                    }
                }
            }

            if (!stepping) index++;      // the loop in StepForward/SeekTo owns it instead
            if (OnIndexChanged != null) OnIndexChanged(index);
            if (mode == TasPlaybackMode.SpectateState) ShowStateAt(index);
            if (index >= length && playing) Finish();
        }

        void ShowStateAt(int tickF)
        {
            if (rig == null || tape == null) return;
            TasCheckpoint s = tape.SampleState(tickF);
            rig.position = s.Position;
            if (rig != null) rig.rotation = Quaternion.Euler(0f, s.bodyYaw, 0f);
            if (rigCam != null) rigCam.transform.rotation = Quaternion.Euler(s.camPitch, s.bodyYaw, 0f);
        }

        void Finish()
        {
            playing = false;
            Detach();
            TasInput.ReleaseToLive();
            TasInput.BlockLiveInput = false;
            TasClock.i.SetMode(TasClockMode.Auto);
            Debug.Log(string.Format("[TAS] playback end ticks={0} divergences={1} corrections={2} mode={3}",
                index, divergences, corrections, mode));
            if (OnFinished != null) OnFinished();
        }

        public string CompatibleReason()
        {
            if (tape == null) return "no tape";
            if (tape.header.tickCount <= 0) return "empty tape";
            if (TasRecorder.i != null && tape.header.configHash != TasRecorder.ComputeConfigHash())
                return "configHash mismatch (tape " + tape.header.configHash.ToString("X8") + " vs build " +
                       TasRecorder.ComputeConfigHash().ToString("X8") + "): gravity/fixedDeltaTime/sensitivity changed";
            if (tape.header.tickRate < 10 || tape.header.tickRate > 1000)
                return "implausible tickRate " + tape.header.tickRate;
            string extra = TasRecorder.Validate(tape);
            if (extra != null) return extra;
            return null;
        }

        /// <summary>The frame that produced the current display state (for the input overlay / editor grid).</summary>
        public TasInputFrame CurrentFrame()
        {
            if (tape == null || tape.frames.Count == 0) return default(TasInputFrame);
            int k = Mathf.Clamp(playing ? index : index - 1, 0, tape.frames.Count - 1);
            return tape.frames[k];
        }

        public TasTape Tape { get { return tape; } }
        public void SetSpeed(double s)
        {
            speed = Mathf.Clamp((float)s, 0.03125, 16f);
            if (TasClock.Exists)
            {
                TasClock.i.speed = speed;
                ChooseMode();
            }
        }

        public void SetPaused(bool p)
        {
            if (!TasClock.Exists) return;
            TasClock.i.paused = p;
            ChooseMode();
        }

        public double RunSeconds { get { return index * (1.0 / Mathf.Max(1, tape != null ? tape.header.tickRate : 60)); } }
    }
}
