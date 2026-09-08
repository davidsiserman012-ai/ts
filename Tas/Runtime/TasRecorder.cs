using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Drop-in replacement for DemoRecorder, same gates and API shape, but it records INPUT
    /// TICKS and it is the object that owns the run's tick list - which is what makes the
    /// rollback / branch / splice cycle of a TAS tool possible.
    ///
    /// Carried over: static i, cfgEnabled, meta2Enabled, the Network.OnMeta2Set gate, raceMode
    /// suppression, the identity snapshot, StartNewRecording / EndRecording, the frame cap.
    /// Fixed: tick-counted sampling, no per-frame ShallowCopy, explicit end-on-cap, and the
    /// previously-dead GetInputData() is now the payload.
    /// Added for a TAS tool: truncation on rollback, branch splice, per-tick savestates, and
    /// a live-tape view so the tool window can paint frames while you play.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public sealed class TasRecorder : MonoBehaviour
    {
        public static bool cfgEnabled = true;
        public static bool meta2Enabled = true;
        public static bool record_touches = true;
        public static TasRecorder i;

        /// <summary>Wire these where you already call DemoRecorder, or leave them null.</summary>
        public static System.Func<bool> RaceModeProbe;
        public static System.Func<uint> ExtraConfigProbe;   // game-side: control type, gravity, boosts...

        /// <summary>
        /// Game-side hooks for values that change what a tick MEANS but that this assembly must not
        /// know about. InputManager.screenInch is one: your touch movement is
        /// screenInch * (delta/Screen.width) * 3, so the same swipe means a different speed on a
        /// different display. Record it, hash it, and pin it during replay so a run made on your dev
        /// machine verifies anywhere.
        /// </summary>
        public static System.Func<float> ScreenInchProbe;
        public static System.Action<float> ScreenInchPin;   // 0f => restore whatever the platform says
        public System.Func<int> SpeedReader;          // () => (int)fpsChar.speed

        [Header("Refs (auto-found if null)")]
        public Transform player;
        public Transform playerCam;
        public Rigidbody playerRb;

        [Header("Limits")]
        public int tickRate = 60;
        public int maxTicks = 216000;
        public int checkpointEvery = 60;

        [Header("Output")]
        public string outDir;
        public bool savestateEveryTick = true;

        [Header("Resim correction")]
        public float snapTolerance = 0.35f;

        // ---- state ----
        public bool recording;
        public bool endedByCap;
        public int frameCount;
        public int speed;
        public uint liveHash = 2166136261u;

        readonly List<TasInputFrame> frames = new List<TasInputFrame>(8192);
        readonly List<TasCheckpoint> checkpoints = new List<TasCheckpoint>(256);
        TasTape tape = new TasTape();
        TasRng rng;

        public event Action<TasTape> OnTapeReady;

        public IReadOnlyList<TasInputFrame> Frames { get { return frames; } }
        public List<TasCheckpoint> CheckpointList { get { return checkpoints; } }
        public int TickCount { get { return frames.Count; } }
        public int CheckpointCount { get { return checkpoints.Count; } }
        public bool CanBranch { get { return frames.Count > 0; } }
        public uint AccumulatorAtParent { get { return liveHash; } }
        public double RunSeconds { get { return frames.Count * (1.0 / Mathf.Max(1, tickRate)); } }
        public TasTape Tape { get { return tape; } }
        public double LastCheckpointTickF { get { return checkpoints.Count > 0 ? checkpoints[checkpoints.Count - 1].tick : -1; } }

        void Awake()
        {
            i = this;
            if (string.IsNullOrEmpty(outDir)) outDir = TasToolConfig.Dir;
            try { Directory.CreateDirectory(outDir); } catch { }
            if (TasClock.Exists) tickRate = TasClock.i.tickRate;
            AutoFind();
        }

        void OnDestroy() { if (i == this) i = null; }

        void AutoFind()
        {
            if (player == null)
            {
                GameObject go = GameObject.FindGameObjectWithTag("Player");
                if (go != null) { player = go.transform; playerRb = go.GetComponent<Rigidbody>(); }
            }
            if (playerCam == null)
            {
                Camera c = Camera.main;
                if (c != null) playerCam = c.transform;
            }
        }

        void Start() { enabled = false; }        // same as DemoRecorder: armed by StartNewRecording

        void OnEnable()
        {
            if (TasClock.Exists) TasClock.i.OnTickCapture += OnCapture;
        }

        void OnDisable()
        {
            if (TasClock.Exists) TasClock.i.OnTickCapture -= OnCapture;
        }

        // ------------------------------------------------------------------ config / identity

        public void ReloadPlayerData()
        {
            TasTapeHeader h = tape.header;
            // ⚠ same fields your DemoRecorder.ReloadPlayerData() fills:
            // h.mapname = MapIsimHelpers.ServerMapIsimGetir();
            // h.nick = Prefs.ServerNick;
            // h.flagId = Prefs.SelectedFlag;  h.avatarId = Prefs.SelectedAvatar;
            // h.knifeId = InventoryHelpers.aktifBicak; h.capeId = InventoryHelpers.aktifCape;
            // h.gloveId = InventoryHelpers.aktifEldiven; h.effectId = InventoryHelpers.aktifPlayerEffect;
            // h.rankStr = GameManagerHelpers.RankBelirle();
            // h.rankIdx = GameManagerHelpers.RankIndexBul(h.rankStr);
            h.gameVersion = Application.version;
            h.screenInch = ScreenInchProbe != null ? ScreenInchProbe() : 0f;
            h.mapHash = TasTape.HashOf(h.mapname);
            h.configHash = ComputeConfigHash();
        }

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
            h = TasRng.Mix(h, Mathf.RoundToInt((ScreenInchProbe != null ? ScreenInchProbe() : 0f) * 10000f));
            h = TasRng.Mix(h, Screen.height);
            h = TasRng.Mix(h, Mathf.RoundToInt(Screen.dpi * 10f));
            if (ExtraConfigProbe != null) h = TasRng.Mix(h, unchecked((int)ExtraConfigProbe()));
            return h;
        }

        // ------------------------------------------------------------------ lifecycle

        public void StartNewRecording()
        {
            if (IsRaceMode()) { enabled = false; return; }
            if (!cfgEnabled || !meta2Enabled) return;

            if (!TasClock.Exists)
            {
                Debug.LogError("[TAS] no TasClock in the scene - add one (TasTool creates it automatically)");
                return;
            }
            if (recording) StopRecording(true);

            TasToolConfig cfg = TasToolConfig.Load();
            tickRate = cfg.tickRate;
            maxTicks = cfg.maxTicks;
            checkpointEvery = cfg.checkpointEvery;
            savestateEveryTick = cfg.snapshotEveryTick;
            TasClock.i.SetTickRate(tickRate);

            Debug.Log("[TAS] StartNewRecording @tick " + TasClock.Tick + "  " + tickRate + "Hz");

            tape = new TasTape();
            tape.header.tickRate = tickRate;
            tape.header.checkpointEvery = Mathf.Max(1, checkpointEvery);
            tape.header.rngSeed = TasRng.FromNow().s;
            rng = new TasRng(tape.header.rngSeed);
            TasSim.Rng = rng;
            TasSim.Accumulator = liveHash = 2166136261u;

            frames.Clear();
            checkpoints.Clear();
            frames.Capacity = Mathf.Max(frames.Capacity, Mathf.Min(maxTicks, 1 << 16));
            frameCount = 0;
            endedByCap = false;

            ReloadPlayerData();

            TasSavestates.Init(cfg.ringCapacity);
            TasSavestates.ClearRing();
            enabled = true;
            recording = true;
        }

        public TasTape EndRecording() { return StopRecording(true); }

        public TasTape StopRecording(bool persist)
        {
            if (!recording) return tape;
            recording = false;
            enabled = false;

            tape.frames = new List<TasInputFrame>(frames);
            tape.checkpoints = new List<TasCheckpoint>(checkpoints);
            tape.BuildIndex();
            tape.metaJson = JsonUtility.ToJson(tape.header);

            Debug.Log("[TAS] EndRecording " + tape.Summary() + (endedByCap ? " (hit maxTicks)" : ""));
            if (persist) SaveTape();
            if (OnTapeReady != null) OnTapeReady(tape);
            return tape;
        }

        public string LastSavedPath { get; private set; }

        /// <summary>
        /// Stamp where the level actually ended. Needed because your demo stop is Olay_OyuncuDustu,
        /// not Olay_LevelTamamlandi: the recording keeps running through the finish panel, the ad,
        /// and the respawn. The tick the player crossed the line is a fact about the run, so the tool
        /// records it and the export can trim to it instead of shipping the wandering.
        /// </summary>
        public void MarkFinish(string reason, int gameSeconds)
        {
            tape.header.finishTick = frames.Count;
            tape.header.finishReason = reason;
            tape.header.gameRunSeconds = gameSeconds;
            if (m_timeManagerSecondsProbe != null) tape.header.gameRunSeconds = m_timeManagerSecondsProbe();
            Debug.Log("[TAS] finish stamped at tick " + tape.header.finishTick + " (" + reason +
                      ") gameSeconds=" + tape.header.gameRunSeconds);
        }

        static System.Func<int> m_timeManagerSecondsProbe;
        public static void BindTimeManagerSeconds(System.Func<int> probe) { m_timeManagerSecondsProbe = probe; }

        /// <summary>Trim the trunk to the stamped finish, so replay/leaderboard end at the line.</summary>
        public bool TrimToFinish()
        {
            int f = tape.header.finishTick;
            if (f <= 0 || f >= frames.Count) return false;
            frames.RemoveRange(f, frames.Count - f);
            for (int k = checkpoints.Count - 1; k >= 0; k--)
                if (checkpoints[k].tick >= f) checkpoints.RemoveAt(k);
            frameCount = frames.Count;
            tape.frames = new List<TasInputFrame>(frames);
            tape.checkpoints = new List<TasCheckpoint>(checkpoints);
            tape.BuildIndex();
            Debug.Log("[TAS] trimmed to finish tick " + f + " -> " + tape.Summary());
            return true;
        }

        public string SaveTape()
        {
            tape.frames = new List<TasInputFrame>(frames);
            tape.checkpoints = new List<TasCheckpoint>(checkpoints);
            tape.BuildIndex();
            string path = Path.Combine(outDir, TasTool.Sanitize(tape.header.nick) + "_" +
                                       tape.header.mapname + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".tas");
            try { TasTape.Write(path, tape); }
            catch (Exception e) { Debug.LogError("[TAS] write failed: " + e.Message); return null; }
            LastSavedPath = path;
            tape.SavedPath = path;
            Debug.Log("[TAS] wrote " + path + " (" + new FileInfo(path).Length + " B)");
            return path;
        }

        // ------------------------------------------------------------------ per-tick

        void OnCapture()
        {
            if (!recording) return;

            if (frames.Count >= maxTicks)
            {
                if (!endedByCap)
                {
                    endedByCap = true;
                    Debug.LogWarning("[TAS] maxTicks (" + maxTicks + ") reached - run ends cleanly, not truncated.");
                    TasTool.NotifyMapFinished("maxTicks");
                }
                return;
            }

            // Fresh read at the capture point, not TasInput.Current (that was committed at tick head,
            // before your controller consumed this frame's input). See TasClock.RunCaptures.
            TasInputFrame f = TasInput.SampleLiveNow();
            if (frames.Count > 0)
            {
                TasInputFrame p = frames[frames.Count - 1];
                // Edges are DERIVED from held bits so that a hand-edited tape and a captured tape
                // compute them identically. Never trust a live GetKeyDown for this.
                f.Set(TasButton.JumpPressed, f.Has(TasButton.Jump) && !p.Has(TasButton.Jump));
                f.Set(TasButton.FirePressed, f.Has(TasButton.Fire) && !p.Has(TasButton.Fire));
            }

            frames.Add(f);
            frameCount = frames.Count;

            liveHash = TasRng.Mix(liveHash, f.buttons);
            liveHash = TasRng.Mix(liveHash, f.lookX);
            liveHash = TasRng.Mix(liveHash, f.aimX | (f.aimY << 16));
            if (player != null)
            {
                liveHash = TasRng.Mix(liveHash, player.position.x);
                liveHash = TasRng.Mix(liveHash, player.position.y);
                liveHash = TasRng.Mix(liveHash, player.position.z);
            }
            TasSim.Accumulator = liveHash;

            int period = Mathf.Max(1, checkpointEvery);
            if (frames.Count == 1 || ((frames.Count - 1) % period) == 0)
                checkpoints.Add(CaptureCheckpoint());

            if (SpeedReader != null) speed = SpeedReader();

            // after the hash/checkpoint bookkeeping so a restored snapshot lands mid-run correctly
            if (savestateEveryTick) TasSavestates.Push();
        }

        TasCheckpoint CaptureCheckpoint()
        {
            TasCheckpoint c = new TasCheckpoint();
            c.tick = frames.Count - 1;
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


        // ------------------------------------------------------------------ rollback plumbing

        /// <summary>
        /// Cut the trunk at tick and make that the live run. `liveHash` is rewound to the value
        /// the run had there, otherwise every checkpoint after the cut looks like a desync.
        /// </summary>
        public bool TruncateTo(int tick, uint hashAtTick)   // hashAtTick: TasSim.Accumulator at that tick
        {
            if (tick < 0 || tick > frames.Count) return false;
            while (frames.Count > tick) frames.RemoveAt(frames.Count - 1);
            for (int k = checkpoints.Count - 1; k >= 0; k--)
                if (checkpoints[k].tick >= tick) checkpoints.RemoveAt(k);
            frameCount = frames.Count;
            liveHash = hashAtTick;
            TasSim.Accumulator = hashAtTick;
            endedByCap = false;
            Debug.Log("[TAS] trunk truncated to tick " + tick + " (" + RunSeconds.ToString("0.000") + "s)");
            return true;
        }

        public bool SpliceTail(int fromTick, List<TasInputFrame> tail, List<TasCheckpoint> tailCk, uint hashAtCut)
        {
            if (fromTick < 0 || fromTick > frames.Count) return false;
            while (frames.Count > fromTick) frames.RemoveAt(frames.Count - 1);
            for (int k = checkpoints.Count - 1; k >= 0; k--)
                if (checkpoints[k].tick >= fromTick) checkpoints.RemoveAt(k);

            for (int k = 0; k < tail.Count; k++) frames.Add(tail[k]);
            // checkpoint ticks are absolute within the trunk, so a sorted merge keeps seeking honest
            for (int k = 0; k < tailCk.Count; k++)
            {
                TasCheckpoint c = tailCk[k];
                int at = checkpoints.Count;
                while (at > 0 && checkpoints[at - 1].tick > c.tick) at--;
                checkpoints.Insert(at, c);
            }

            frameCount = frames.Count;
            liveHash = hashAtCut;
            for (int k = 0; k < tail.Count; k++)
            {
                TasInputFrame f = tail[k];
                liveHash = TasRng.Mix(liveHash, f.buttons);
                liveHash = TasRng.Mix(liveHash, f.lookX);
            }
            TasSim.Accumulator = liveHash;
            return true;
        }

        public void LoadIntoTrunk(TasTape t)
        {
            frames.Clear(); checkpoints.Clear();
            if (t != null)
            {
                for (int k = 0; k < t.frames.Count; k++) frames.Add(t.frames[k]);
                for (int k = 0; k < t.checkpoints.Count; k++) checkpoints.Add(t.checkpoints[k]);
            }
            frameCount = frames.Count;
            tape = t ?? new TasTape();
        }

        static bool IsRaceMode() { return RaceModeProbe != null && RaceModeProbe(); }

        public void GetTasJson()
        {
            if (frames.Count == 0) { Debug.Log("[TAS] empty"); return; }
            var sb = new System.Text.StringBuilder();
            int n0 = Mathf.Max(0, frames.Count - 12);
            for (int k = n0; k < frames.Count; k++) sb.Append(k).Append(": ").Append(frames[k].Pretty()).Append('\n');
            Debug.Log("[TAS] tail " + frames.Count + "\n" + sb);
        }
    }
}
