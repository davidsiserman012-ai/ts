using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Tas
{
    public enum TasSession
    {
        Booted,
        WaitingForGameStart,
        Recording,
        Replaying,
        Finished,
    }

    /// <summary>
    /// The PC TAS tool itself: one GameObject, created for you at runtime, no scene wiring.
    /// Everything you asked for lives in here:
    ///
    ///  - auto-record the moment the game starts, auto-stop when the map finishes
    ///  - save states: one per tick in RAM (Backspace) + 10 slots on disk (1..9,0 / Shift+digit)
    ///  - slow game speed (0.03125x .. 8x) and single-frame stepping
    ///  - a replay toggle: while it is on, every game start replays the last recorded run
    ///    in realtime from tick 0
    ///  - rollback branches are spliced, not appended, so the trunk tape stays ONE contiguous
    ///    tick range - which is why "replay everything as if it was all one run" needs no
    ///    special code path in the player at all.
    /// </summary>
    [DefaultExecutionOrder(-33000)]
    public sealed class TasTool : MonoBehaviour
    {
        public static TasTool i;
        public TasToolConfig cfg = new TasToolConfig();
        public TasSession session = TasSession.Booted;
        public TasBranchStore branches = new TasBranchStore();
        public string lastRunPath;
        public string statusLine = "idle";
        public int rollbackCount;

        static readonly System.Collections.Generic.List<TasTickDriver> drivers =
            new System.Collections.Generic.List<TasTickDriver>(4);

        /// <summary>Game-side hook: the bridge uses this to start the persistence freeze.</summary>
        public event Action OnRecordStart;

        TasClock clock;
        TasRecorder recorder;
        TasPlayback playback;
        TasOverlay overlay;
        TasToolUI ui;
        float startedAtWall;
        string sessionReason;
        bool dirtyRun;

        // ---------------------------------------------------------------- bootstrap

        /// <summary>
        /// RuntimeInitializeOnLoadMethod means the tool exists in every scene, including the
        /// menu, with zero wiring - and it never gets destroyed on scene load because it is
        /// created by the scene that is loading (see the DontDestroyOnLoad call).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBoot()
        {
            if (i != null) { i.OnSceneReady(); return; }
            GameObject go = new GameObject("TasTool");
            DontDestroyOnLoad(go);
            TasTool tool = go.AddComponent<TasTool>();
            tool.earlyBoot = true;
            SceneManager.sceneLoaded += tool.OnSceneLoaded;
        }

        bool earlyBoot;

        void Awake()
        {
            if (i != null && i != this) { Destroy(gameObject); return; }
            i = this;
            DontDestroyOnLoad(gameObject);

            cfg = TasToolConfig.Load();
            if (!cfg.toolEnabled) { enabled = false; return; }

            clock = GetComponent<TasClock>();
            if (clock == null) clock = gameObject.AddComponent<TasClock>();
            clock.enforceFixedRate = cfg.enforceFixedRate;
            clock.SetTickRate(cfg.tickRate);

            if (recorder == null) recorder = GetComponent<TasRecorder>() ?? gameObject.AddComponent<TasRecorder>();
            if (playback == null) playback = GetComponent<TasPlayback>() ?? gameObject.AddComponent<TasPlayback>();
            if (overlay == null) overlay = GetComponent<TasOverlay>() ?? gameObject.AddComponent<TasOverlay>();
            if (ui == null) ui = GetComponent<TasToolUI>() ?? gameObject.AddComponent<TasToolUI>();

            TasLiveInput.Install();
            playback.OnFinished += LoopReplay;
            TasSavestates.Init(cfg.ringCapacity);
            TasSavestates.RefreshSlotMeta();
            if (!Application.isEditor && !IsDevBuild()) cfg.panelVisible = false;

            enabled = true;
        }

        static bool IsDevBuild()
        {
            return Debug.isDebugBuild || (Application.isEditor);
        }

        void OnEnable()
        {
            if (clock != null) clock.OnTickTail += OnTail;
            if (!earlyBoot) SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneReady();
        }

        void OnDisable()
        {
            if (clock != null) clock.OnTickTail -= OnTail;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void OnDestroy()
        {
            if (i == this) i = null;
        }

        void OnSceneLoaded(Scene s, LoadSceneMode m) { earlyBoot = false; OnSceneReady(); }

        /// <summary>
        /// Which scenes count as "the game started" is the one thing I cannot know, so the rule is:
        /// any scene load that is not a known menu/loader starts the session. Override it exactly
        /// once, next to where you already call DemoRecorder.StartNewRecording().
        /// </summary>
        public static System.Func<string, bool> IsPlayScene;
        public static readonly string[] MenuSceneGuesses = { "menu", "main", "loader", "boot", "lobby", "login", "startup", "scene" };

        void OnSceneReady()
        {
            if (session == TasSession.Recording || session == TasSession.Replaying) return;
            string name = SceneManager.GetActiveScene().name.ToLowerInvariant();
            bool play = IsPlayScene != null ? IsPlayScene(name) : LooksLikePlayScene(name);
            if (play) MarkGameStarted();
            else statusLine = "in '" + name + "' - waiting for a play scene";
        }

        static bool LooksLikePlayScene(string lower)
        {
            for (int k = 0; k < MenuSceneGuesses.Length; k++)
                if (lower.Contains(MenuSceneGuesses[k])) return false;
            return true;
        }

        // ---------------------------------------------------------------- session control

        /// <summary>Call this from wherever you currently call DemoRecorder.StartNewRecording().</summary>
        public void MarkGameStarted()
        {
            if (session == TasSession.Recording || session == TasSession.Replaying) return;
            startedAtWall = (double)Time.unscaledTime;
            sessionReason = null;
            dirtyRun = false;
            branches.Clear();

            if (cfg.autoReplayOnGameStart && HasReplaySource())
            {
                TasTape t = LoadLastRun();
                if (t != null)
                {
                    LoadTape(t);
                    bool ok = playback.Play(t, 0, cfg.replayMode);
                    session = ok ? TasSession.Replaying : TasSession.WaitingForGameStart;
                    statusLine = ok
                        ? "REPLAY " + t.Summary()
                        : "replay requested but playback refused (see console)";
                    return;
                }
                statusLine = "replay toggle ON but no tape found - recording instead";
            }

            if (cfg.autoRecordOnGameStart)
            {
                if (OnRecordStart != null) OnRecordStart();
                recorder.StartNewRecording();
                session = TasSession.Recording;
                statusLine = "RECORDING from game start (" + cfg.tickRate + "Hz, fixed-rate " +
                             (cfg.enforceFixedRate ? "on" : "OFF") + ")";
            }
            else session = TasSession.WaitingForGameStart;
        }

        /// <summary>Call this from wherever you currently call DemoRecorder.EndRecording().</summary>
        public void MarkMapFinished(string reason)
        {
            if (session == TasSession.Replaying) { playback.Stop(); session = TasSession.Finished; statusLine = "replay ended (" + reason + ")"; return; }
            if (session != TasSession.Recording) return;
            sessionReason = reason;
            TasTape t = recorder.EndRecording();
            if (cfg.trimToFinishTick && t != null && t.header.finishTick > 0) recorder.TrimToFinish();
            session = TasSession.Finished;
            if (t != null && t.frames.Count > 0)
            {
                lastRunPath = TasToolConfig.LastRunPath;
                try { File.WriteAllText(TasToolConfig.LastRunPath, recorder.LastSavedPath ?? ""); } catch { }
                AppendLibrary(t);
                if (!string.IsNullOrEmpty(t.SavedPath))
                    branches.Write(Path.ChangeExtension(t.SavedPath, ".branches.bin"));
                statusLine = "saved " + t.Summary();
            }
            else statusLine = "run finished with no frames captured (" + reason + ")";
        }

        void LoopReplay()
        {
            if (!cfg.loopReplay || session != TasSession.Replaying) return;
            playback.Play(null, 0, cfg.replayMode);
        }

        static void AppendLibrary(TasTape t)
        {
            try
            {
                File.AppendAllText(TasToolConfig.LibraryPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + t.header.mapname + "\t" +
                    t.header.runSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "s\t" +
                    t.header.tickCount + "t\tcfg=" + t.header.configHash.ToString("X8") + "\n");
            }
            catch (Exception e) { Debug.LogWarning("[TAS] library: " + e.Message); }
        }

        // ---------------------------------------------------------------- tape in / out

        public TasTape LoadLastRun()
        {
            try
            {
                if (File.Exists(TasToolConfig.LastRunPath))
                {
                    string p = File.ReadAllText(TasToolConfig.LastRunPath).Trim();
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) return TasTape.Read(p);
                }
            }
            catch (Exception e) { Debug.LogWarning("[TAS] last run: " + e.Message); }

            // fall back to the newest .tas in the folder
            try
            {
                string[] all = Directory.GetFiles(TasToolConfig.Dir, "*.tas", SearchOption.AllDirectories);
                DateTime newest = DateTime.MinValue; string best = null;
                for (int k = 0; k < all.Length; k++)
                {
                    DateTime w = File.GetLastWriteTimeUtc(all[k]);
                    if (w > newest) { newest = w; best = all[k]; }
                }
                if (best != null) return TasTape.Read(best);
            }
            catch (Exception e) { Debug.LogWarning("[TAS] scan: " + e.Message); }
            return null;
        }

        TasTape lastTape;

        public TasTape LastTape { get { return lastTape; } }

        public void LoadTape(TasTape t)
        {
            lastTape = t;
            recorder.LoadIntoTrunk(t);
            playback.Load(t);
            dirtyRun = false;
        }

        /// <summary>Hand the platform's own value back when the tape lets go.</summary>
        public void RestoreScreenInch()
        {
            if (TasRecorder.ScreenInchPin != null) TasRecorder.ScreenInchPin(0f);
        }

        public void SaveRun()
        {
            string p = recorder.SaveTape();
            if (p != null)
            {
                try { File.WriteAllText(TasToolConfig.LastRunPath, p); } catch { }
                lastRunPath = p;
                statusLine = "saved " + p;
                dirtyRun = false;
            }
        }

        // ---------------------------------------------------------------- transport

        public void ToggleRecord()
        {
            if (session == TasSession.Recording) MarkMapFinished("manual stop");
            else { session = TasSession.WaitingForGameStart; MarkGameStarted(); }
        }

        public void ToggleReplay()
        {
            cfg.autoReplayOnGameStart = !cfg.autoReplayOnGameStart;
            cfg.Save();
            statusLine = "replay on game start: " + (cfg.autoReplayOnGameStart ? "ON" : "OFF");
            if (cfg.autoReplayOnGameStart && session == TasSession.Recording) MarkMapFinished("replay toggled");
        }

        public void PlayNow(int fromTick)
        {
            TasTape t = recorder.Tape != null && recorder.Tape.frames.Count > 0 ? recorder.Tape : LoadLastRun();
            if (t == null) { statusLine = "nothing to play"; return; }
            LoadTape(t);
            if (session == TasSession.Recording) { recorder.StopRecording(false); }
            playback.Play(t, fromTick, cfg.replayMode);
            session = TasSession.Replaying;
            statusLine = "replaying from tick " + fromTick;
        }

        public void StopTransport()
        {
            if (session == TasSession.Replaying) playback.Stop();
            if (session == TasSession.Recording) MarkMapFinished("manual stop");
            clock.SetMode(TasClockMode.Auto);
            clock.paused = false;
            clock.speed = 1.0;
            session = TasSession.Finished;
            statusLine = "stopped";
        }

        /// <summary>
        /// The rollback rule that makes a TAS run one continuous tape:
        /// stash the discarded tail as a branch, rewind the sim, THEN truncate the trunk.
        /// Any other order loses data or leaves the hash chain pointing at the wrong tick.
        /// </summary>
        public bool RollbackTo(int tick)
        {
            TasStateEntry e;
            if (!TasSavestates.TryRollback(tick, out e))
            {
                statusLine = "no savestate for tick " + tick + " (ring holds " + cfg.ringCapacity + " ticks)";
                return false;
            }
            if (session == TasSession.Replaying)
            {
                playback.SeekTo(tick);
                rollbackCount++;
                return true;
            }
            if (recorder != null && recorder.recording && tick < recorder.TickCount)
            {
                if (cfg.keepDiscardedTailAsBranch) branches.Cut(tick, e.accumulator, "after " + rollbackCount);
                recorder.TruncateTo(tick, e.accumulator);
                if (cfg.resetVisualsOnLoad) TasVisualReset.Reset();
                dirtyRun = true;
            }
            rollbackCount++;
            statusLine = "rollback -> tick " + tick + " (" + TasSavestates.LastSnapshotBytes + " B state)";
            return true;
        }

        public void RollbackTicks(int n)
        {
            int t = Mathf.Max(0, (int)TasClock.Tick - n);
            RollbackTo(t);
        }

        public void Step(int n)
        {
            if (session == TasSession.Replaying) { playback.StepForward(n); return; }
            clock.SetMode(TasClockMode.Hold);
            clock.paused = true;
            if (n > 0) for (int k = 0; k < n; k++) clock.Step();
            else for (int k = 0; k < -n; k++) RollbackTo(Mathf.Max(0, (int)TasClock.Tick - 1));
        }

        public void SetSpeed(float s)
        {
            cfg.speed = Mathf.Clamp(s, 0.03125f, 8f);
            clock.speed = cfg.speed;
            playback.SetSpeed(cfg.speed);
            // below 1x we must own the step; above 1x too. Back to 1x during recording hands
            // the world back to the engine so recording still runs at native feel.
            bool toolish = Mathf.Abs(cfg.speed - 1f) > 0.001f;
            // below 1x -> Hold (cancels non-tick frames). above 1x -> Manual (we own the step,
            // which needs every sim script on the tick bus). exactly 1x -> Auto.
            if (session == TasSession.Replaying) playback.SetSpeed(cfg.speed);
            else if (session == TasSession.Recording)
            {
                if (cfg.speed > 1.001f) clock.SetMode(TasClockMode.Manual);
                else if (cfg.speed < 0.999f) clock.SetMode(TasClockMode.Hold);
                else clock.SetMode(TasClockMode.Auto);
                clock.speed = cfg.speed;
                if (toolish) clock.paused = false;
            }
            statusLine = "speed " + TasToolConfig.HumanSpeed(cfg.speed) +
                         (toolish ? "  (manual stepping on)" : "  (engine cadence)");
            cfg.Save();
        }

        public void TogglePause()
        {
            clock.paused = !clock.paused;
            statusLine = clock.paused ? "paused (frame-step with . / ,)" : "running";
        }

        public int SaveSlot(int slot)
        {
            int r = TasSavestates.SaveSlot(slot, statusLine, -1);
            statusLine = r >= 0 ? ("saved slot " + slot + " @tick " + TasClock.Tick) : "slot save failed";
            return r;
        }

        public bool LoadSlot(int slot)
        {
            TasSlotInfo info = TasSavestates.SlotInfo(slot);
            int tick = info != null ? (int)info.tick : (int)TasClock.Tick;
            if (TasSavestates.LoadSlot(slot) == null) { statusLine = "slot " + slot + " load failed"; return false; }
            if (session == TasSession.Recording && tick < recorder.TickCount)
            {
                if (cfg.keepDiscardedTailAsBranch) branches.Cut(tick, TasSim.Accumulator, "slot" + slot);
                recorder.TruncateTo(tick, TasSim.Accumulator);
                dirtyRun = true;
            }
            else if (session == TasSession.Replaying) playback.SeekTo(tick);
            if (cfg.resetVisualsOnLoad) TasVisualReset.Reset();
            statusLine = "loaded slot " + slot + " @tick " + tick;
            return true;
        }

        // ---------------------------------------------------------------- per-tick + hotkeys

        void OnTail()
        {
            // the recorder already pushed when it is recording (that one has the fresh hash);
            // this covers playback-only sessions so Backspace works while watching a replay
            if (cfg.snapshotEveryTick && session == TasSession.Replaying) TasSavestates.Push();
        }

        void Update()
        {
            if (!cfg.toolEnabled) return;
            ReadHotkeys();

            if (dirtyRun && Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.S)) SaveRun();
        }

        void ReadHotkeys()
        {
            if (KeyDown(cfg.keyPanel)) { cfg.panelVisible = !cfg.panelVisible; cfg.Save(); }
            if (KeyDown(cfg.keyRecord)) ToggleRecord();
            if (KeyDown(cfg.keyStop)) StopTransport();
            if (KeyDown(cfg.keyReplay)) ToggleReplay();
            if (KeyDown(cfg.keyPause)) TogglePause();
            if (KeyDown(cfg.keyStepFwd)) Step(1);
            if (KeyDown(cfg.keyStepBack)) Step(-1);
            if (KeyDown(cfg.keySlowDown)) SetSpeed(cfg.speed <= 1f ? cfg.speed * 0.5f : cfg.speed * 0.5f);
            if (KeyDown(cfg.keySlowUp)) SetSpeed(cfg.speed * 2f);
            if (KeyDown(cfg.keySlowReset)) SetSpeed(1f);
            if (KeyDown(cfg.keyRollbackLast)) RollbackTicks(30);

            if (Input.GetKeyDown(cfg.keyQuickSave))
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (ctrl) LoadSlot(0); else SaveSlot(0);
            }

            for (int k = 0; k <= 9; k++)
            {
                KeyCode key = (KeyCode)(System.Convert.ToInt32(KeyCode.Alpha1) + k);
                if (k == 9) key = KeyCode.Alpha0;
                if (Input.GetKeyDown(key))
                {
                    bool load = cfg.keySlotLoadModifier != KeyCode.None && Input.GetKey(cfg.keySlotLoadModifier);
                    int slot = k == 9 ? 9 : k;
                    if (load) LoadSlot(slot); else SaveSlot(slot);
                }
            }
        }

        static bool KeyDown(KeyCode k)
        {
            return k != KeyCode.None && Input.GetKeyDown(k);
        }

        // ---------------------------------------------------------------- misc

        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "anon";
            char[] bad = Path.GetInvalidFileNameChars();
            for (int k = 0; k < bad.Length; k++) if (s.IndexOf(bad[k]) >= 0) s = s.Replace(bad[k].ToString(), "");
            return s;
        }

        /// <summary>
        /// Register a sim script so slow-mo/frame-step/turbo can drive it. Call once at boot:
        /// TasTool.RegisterTickDriver(fpsController, "Update");
        /// </summary>
        public static TasTickDriver RegisterTickDriver(MonoBehaviour target, string method = "Update")
        {
            return TasTickDriver.Attach(target, method);
        }

        public static void RegisterTickDriver(TasTickDriver d)
        {
            if (d != null && !drivers.Contains(d)) drivers.Add(d);
        }

        public static System.Collections.Generic.IReadOnlyList<TasTickDriver> Drivers { get { return drivers; } }

        public static void NotifyMapFinished(string reason)
        {
            if (i != null) i.MarkMapFinished(reason);
        }

        /// <summary>Static entry points so you can wire the tool from anywhere, including before
        /// the first scene's objects exist. No-op if the tool is disabled in this build.</summary>
        /// <summary>
        /// Optional frame-exact finish stamp: call it from GameManager.Olay_LevelTamamlandi (the
        /// moment b_levelTamamlandi is set), because OnLevelCompleted is raised 0.1 s later by an
        /// Invoke and only after the fall. Without it the trim uses the fall-time tick, which is a
        /// few ticks past the line - visible in the replay, invisible in the time.
        /// </summary>
        public static void NotifyFinishExact(string reason)
        {
            if (TasRecorder.i == null) return;
            int secs = 0;
            if (TasClock.Exists) secs = (int)(TasClock.Tick / TasClock.Rate);
            TasRecorder.i.MarkFinish(reason, secs);
            if (i != null) i.statusLine = "finish stamped @tick " + TasRecorder.i.TickCount + " (" + reason + ")";
        }

        public static void NotifyGameStarted()
        {
            if (i != null) i.MarkGameStarted();
        }

        public static void NotifyMenuEntered()
        {
            if (i == null) return;
            if (i.session == TasSession.Recording) i.MarkMapFinished("left play scene");
            else if (i.session == TasSession.Replaying) i.StopTransport();
            i.session = TasSession.WaitingForGameStart;
        }

        /// <summary>Persisted one-shot: turn the replay toggle on/off from your options menu.</summary>
        public static bool ReplayToggle
        {
            get { return i != null ? i.cfg.autoReplayOnGameStart : TasToolConfig.Load().autoReplayOnGameStart; }
            set
            {
                TasToolConfig c = i != null ? i.cfg : TasToolConfig.Load();
                if (c.autoReplayOnGameStart == value) return;
                if (i != null) i.ToggleReplay();
                else { c.autoReplayOnGameStart = value; c.Save(); }
            }
        }

        public bool HasReplaySource()
        {
            try
            {
                if (File.Exists(TasToolConfig.LastRunPath)) return true;
                return Directory.GetFiles(TasToolConfig.Dir, "*.tas", SearchOption.AllDirectories).Length > 0;
            }
            catch { return false; }
        }

        public TasRecorder Recorder { get { return recorder; } }
        public TasPlayback Playback { get { return playback; } }
        public TasClock Clock { get { return clock; } }
        public bool ReplayArmed { get { return cfg.autoReplayOnGameStart; } }
        public double SessionSeconds { get { return (double)Time.unscaledTime - startedAtWall; } }

        public string DebugLine
        {
            get
            {
                return string.Format("[TAS] {0}  tick={1} t={2:0.000}s  frames={3}  ring={4}/{5}  branches={6}  speed={7}  {8}",
                    session, TasClock.Tick, clock != null ? clock.simTime : 0.0,
                    recorder != null ? recorder.TickCount : 0,
                    TasSavestates.RingCount, cfg.ringCapacity, branches.Count,
                    TasToolConfig.HumanSpeed(cfg.speed), statusLine);
            }
        }

        void OnGUI()
        {
            if (!cfg.panelVisible) return;
            if (overlay == null) return;
        }
    }
}
