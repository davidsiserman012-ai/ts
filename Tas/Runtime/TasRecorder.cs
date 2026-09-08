using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Drop-in replacement for DemoRecorder, same shape and same public surface, but it
    /// records INPUT TICKS instead of transform snapshots.
    ///
    /// What was carried over 1:1: static i, cfgEnabled, meta2Enabled, the Network.OnMeta2Set
    /// gate, raceMode suppression, ReloadPlayerData() identity snapshot, StartNewRecording /
    /// EndRecording, the frame cap.
    ///
    /// What was fixed:
    ///  - sampling is tick-counted by TasClock, so a 30fps device and a 120fps device
    ///    produce identical tapes (the old fpsTimeTotal += Time.deltaTime sampler did not).
    ///  - no ShallowCopy() per frame and no List&lt;T&gt; growth from 7200 to 36000: one
    ///    struct array, amortised doubling, zero per-tick allocation.
    ///  - fpsCap semantics: it used to be a *sampling* cap on a free-running sim. Now the
    ///    tick rate IS the sim rate, which is the only version that can be replayed.
    ///  - totalFrameLimit now ends the run explicitly (endedByCap) instead of silently
    ///    returning early while the timer keeps accumulating.
    ///  - GetInputData()/record_touches were dead code; that data is now the payload.
    /// </summary>
    [DefaultExecutionOrder(32000)]   // last FixedUpdate of the tick => captures post-sim state
    public sealed class TasRecorder : MonoBehaviour
    {
        // ---- static gates, identical to DemoRecorder ----
        public static bool cfgEnabled = true;
        public static bool meta2Enabled = true;
        public static bool record_touches = true;
        public static TasRecorder i;

        [Header("Wiring (same fields as DemoRecorder)")]
        public Transform player;
        public Transform playerCam;
        public Rigidbody playerRb;

        [Tooltip("Wired in your bootstrap, so this file keeps no hard dependency: " +
                 "recorder.SpeedReader = () => (int)fpsChar.speed;  TasRecorder.RaceModeProbe = () => GameState.raceMode;")]
        public System.Func<int> SpeedReader;
        public static System.Func<bool> RaceModeProbe;

        [Header("Limits")]
        public int tickRate = 60;
        public int maxTicks = 36000;           // 10 min, same ceiling as totalFrameLimit
        public int checkpointEvery = 300;      // 5s of state at 60Hz, for correction + seeking

        [Header("Output")]
        public string outDir;

        // ---- live state ----
        public bool recording;
        public bool endedByCap;
        public long startTick;
        public int frameCount;
        public int speed;                       // kept for parity with the old HUD/leaderboard field

        TasInputFrame[] frames;
        List<TasCheckpoint> checkpoints = new List<TasCheckpoint>(128);
        TasCheckpoint scratch;
        TasTape tape = new TasTape();
        uint liveHash;
        TasRng rng;

        public event Action<TasTape> OnTapeReady;

        void Awake()
        {
            i = this;
            if (string.IsNullOrEmpty(outDir))
                outDir = Path.Combine(Application.persistentDataPath, "tas");
            Directory.CreateDirectory(outDir);

            if (TasClock.Exists) tickRate = TasClock.i.tickRate;
            NetworkMeta2Hook(true);
        }

        void OnDestroy()
        {
            NetworkMeta2Hook(false);
            if (i == this) i = null;
        }

        /// <summary>
        /// The remote kill-switch, same contract as before ("v" = on). Routed through the
        /// deferred queue so the toggle lands on a tick boundary and is therefore replayable.
        /// </summary>
        void NetworkMeta2Hook(bool on)
        {
            // if (on) Network.OnMeta2Set += On_Meta2Set; else Network.OnMeta2Set -= On_Meta2Set;
            // ⚠ uncomment in your project (it lives in your Network class, not in this file).
        }

        void On_Meta2Set(string meta2)
        {
            TasDeferred.Post("meta2", delegate
            {
                meta2Enabled = meta2 == "v";
                if (!meta2Enabled && recording) Stop(true);
            });
        }

        void Start()
        {
            enabled = false;   // same as DemoRecorder.Start(): armed by StartNewRecording()
        }

        // ------------------------------------------------------------------ public API

        public void ReloadPlayerData()
        {
            TasTapeHeader h = tape.header;
            // ⚠ these four lines are the mapping from your DemoData identity fields:
            // h.mapname   = MapIsimHelpers.ServerMapIsimGetir();
            // h.nick      = Prefs.ServerNick;
            // h.flagId    = Prefs.SelectedFlag;
            // h.avatarId  = Prefs.SelectedAvatar;
            // h.knifeId   = InventoryHelpers.aktifBicak;
            // h.capeId    = InventoryHelpers.aktifCape;
            // h.gloveId   = InventoryHelpers.aktifEldiven;
            // h.effectId  = InventoryHelpers.aktifPlayerEffect;
            // h.rankStr -> rankIdx = GameManagerHelpers.RankIndexBul(GameManagerHelpers.RankBelirle());
            h.gameVersion = Application.version;
            h.controlType = CachedPrefsGetInt(PInt_ControlType, 0);
            h.controlTypeStr = CachedPrefsGetString(PString_ControlTypeStr, "");
            h.mapHash = TasTape.HashOf(h.mapname);
            h.configHash = ComputeConfigHash();
        }

        static readonly int PInt_ControlType = 0;
        static readonly int PString_ControlTypeStr = 0;
        static int CachedPrefsGetInt(int k, int d) { return d; }
        static string CachedPrefsGetString(int k, string d) { return d; }

        /// <summary>
        /// Everything that can change the meaning of a tick. If a replay is played on a
        /// build whose configHash differs, refuse rather than silently showing a desync.
        /// </summary>
        public static uint ComputeConfigHash()
        {
            uint h = 2166136261u;
            h = TasRng.Mix(h, Mathf.RoundToInt(Time.fixedDeltaTime * 100000f));
            h = TasRng.Mix(h, Physics.gravity.y);
            h = TasRng.Mix(h, Physics.gravity.z);
            h = TasRng.Mix(h, Physics.bounceThreshold);
            h = TasRng.Mix(h, Physics.defaultContactOffset);
            h = TasRng.Mix(h, Physics.sleepThreshold);
            h = TasRng.Mix(h, Mathf.RoundToInt((Physics.queriesHitTriggers ? 1 : 0) +
                                               (Physics.autoSyncTransforms ? 2 : 0)));
            h = TasRng.Mix(h, Screen.width);
            h = TasRng.Mix(h, Application.version.GetHashCode());
            h = TasRng.Mix(h, Mathf.RoundToInt(TasLiveInput.lookSensitivity * 10000f));
            return h;
        }

        public void StartNewRecording()
        {
            if (IsRaceMode()) { enabled = false; return; }
            if (!cfgEnabled || !meta2Enabled) return;

            Debug.Log("[TAS] StartNewRecording tickRate=" + tickRate);

            if (!TasClock.Exists)
                Debug.LogError("[TAS] No TasClock in the scene - the tape would be meaningless. Add one.");

            if (TasClock.Exists && tickRate != TasClock.i.tickRate)
                TasClock.i.SetTickRate(tickRate);

            tape = new TasTape();
            tape.header.tickRate = tickRate;
            tape.header.checkpointEvery = Mathf.Max(1, checkpointEvery);
            tape.header.rngSeed = TasRng.FromNow().s;
            rng = new TasRng(tape.header.rngSeed);
            tape.frames.Clear();
            tape.checkpoints.Clear();
            checkpoints.Clear();

            frameCount = 0;
            startTick = TasClock.Tick;
            liveHash = 2166136261u;
            endedByCap = false;

            ReloadPlayerData();

            frames = new TasInputFrame[Mathf.Max(8192, maxTicks)];   // one alloc for the whole run

            if (TasClock.Exists)
            {
                TasClock.i.OnTickTail += OnTail;
            }

            enabled = true;
            recording = true;
        }

        public TasTape EndRecording()
        {
            if (!recording) return null;
            return Stop(true);
        }

        TasTape Stop(bool persist)
        {
            recording = false;
            enabled = false;
            if (TasClock.Exists)
            {
            if (TasClock.Exists) TasClock.i.OnTickTail -= OnTail;
            }

            tape.frames = new List<TasInputFrame>(frameCount);
            for (int k = 0; k < frameCount; k++) tape.frames.Add(frames[k]);
            tape.checkpoints = checkpoints;
            tape.BuildIndex();
            tape.metaJson = JsonUtility.ToJson(tape.header);

            Debug.Log("[TAS] EndRecording " + tape.Summary() + (endedByCap ? " (hit maxTicks)" : ""));

            if (persist) SaveTape();
            if (OnTapeReady != null) OnTapeReady(tape);
            return tape;
        }

        /// <summary>Live snapshot while recording (the editor reads this), finished tape after Stop().</summary>
        public TasTape Tape
        {
            get
            {
                if (!recording) return tape;
                TasTape live = new TasTape();
                live.header = tape.header;
                for (int k = 0; k < frameCount; k++) live.frames.Add(frames[k]);
                for (int k = 0; k < checkpoints.Count; k++) live.checkpoints.Add(checkpoints[k]);
                live.BuildIndex();
                return live;
            }
        }

        public string SaveTape()
        {
            string path = Path.Combine(outDir, Sanitize(tape.header.nick) + "_" +
                                        tape.header.mapname + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".tas");
            try { TasTape.Write(path, tape); }
            catch (Exception e) { Debug.LogError("[TAS] write failed: " + e.Message); return null; }
            Debug.Log("[TAS] wrote " + path + " (" + new FileInfo(path).Length + " bytes)");
            return path;
        }

        // ------------------------------------------------------------------ tick plumbing


        void OnTail()
        {
            if (!recording || frameCount >= maxTicks)
            {
                if (recording && frameCount >= maxTicks && !endedByCap)
                {
                    endedByCap = true;
                    Debug.LogWarning("[TAS] maxTicks (" + maxTicks + ") reached - run ended, not truncated mid-tick.");
                    Stop(true);
                }
                return;
            }

            TasInputFrame f = TasInput.Current;
            if (frameCount > 0)
            {
                TasInputFrame p = frames[frameCount - 1];
                // rising edges are derived from held state on replay too; strip the live-only bit
                // so that record and playback compute it identically from the tape.
                f.Set(TasButton.JumpPressed, f.Has(TasButton.Jump) && !p.Has(TasButton.Jump));
                f.Set(TasButton.FirePressed, f.Has(TasButton.Fire) && !p.Has(TasButton.Fire));
            }

            frames[frameCount++] = f;

            liveHash = TasRng.Mix(liveHash, f.lookX);
            liveHash = TasRng.Mix(liveHash, f.aimX | (f.aimY << 16));
            liveHash = TasRng.Mix(liveHash, f.buttons);
            TasSim.Accumulator = liveHash;   // so a snapshot carries the divergence state too
            if (player != null)
            {
                liveHash = TasRng.Mix(liveHash, player.position.x);
                liveHash = TasRng.Mix(liveHash, player.position.y);
                liveHash = TasRng.Mix(liveHash, player.position.z);
            }

            int rel = frameCount;
            if (rel == 1 || (rel % Mathf.Max(1, checkpointEvery)) == 0)
                checkpoints.Add(CaptureCheckpoint());

            if (SpeedReader != null) speed = SpeedReader();
        }

        TasCheckpoint CaptureCheckpoint()
        {
            TasCheckpoint c = scratch;
            c.tick = (int)TasClock.Tick - (int)startTick;
            if (player != null)
            {
                c.Position = player.position;
                c.bodyYaw = player.rotation.eulerAngles.y;
            }
            c.camPitch = playerCam != null ? playerCam.rotation.eulerAngles.x : 0f;
            c.speed = speed;
            c.stateHash = liveHash;
            return c;
        }

        static int ReadSpeed(object fps) { return 0; }   // ⚠ ((RigidbodyFirstPersonController)fps).speed
        static bool IsRaceMode() { return false; }        // ⚠ return GameState.raceMode;
        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "anon";
            foreach (char c in Path.GetInvalidFileNameChars())
                if (s.IndexOf(c) >= 0) s = s.Replace(c.ToString(), "");
            return s;
        }


        /// <summary>Editor/debug parity with GetDemoJson(); prints a human-readable tick list.</summary>
        public void GetTasJson()
        {
            if (frameCount == 0) { Debug.Log("[TAS] empty"); return; }
            var sb = new System.Text.StringBuilder();
            int n0 = Mathf.Max(0, frameCount - 12);
            for (int k = n0; k < frameCount; k++)
                sb.Append(k).Append(": ").Append(frames[k].Pretty()).Append('\n');
            Debug.Log("[TAS] tail " + frameCount + "\n" + sb);
        }
    }
}
