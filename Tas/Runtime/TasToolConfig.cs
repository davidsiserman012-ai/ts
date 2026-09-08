using System;
using System.IO;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Everything the tool does is driven from this file, and it survives restarts, because a
    /// TAS session spans many play attempts. Stored at persistentDataPath/tas/config.json so a
    /// PC build works with no scene wiring; the editor reads/writes the same file.
    /// </summary>
    [Serializable]
    public class TasToolConfig
    {
        [Header("Master")]
        public bool toolEnabled = true;          // release builds: false => no hotkeys, no panel, no capture
        public bool panelVisible = false;

        [Header("Auto capture")]
        public bool autoRecordOnGameStart = true;
        public bool autoStopOnMapFinish = true;
        public bool trimToFinishTick = true;      // drop the post-finish wandering from the run
        public bool rollbackOnDeath = false;      // opt-in: keep the session alive after a fall
        public bool autoReplayOnGameStart = false;   // THE replay toggle
        public TasPlaybackMode replayMode = TasPlaybackMode.ResimLive;
        public bool replayFromStartEveryTime = true;
        public bool loopReplay = false;

        [Header("Timing")]
        public int tickRate = 60;
        public bool enforceFixedRate = true;     // see TasClock.enforceFixedRate

        [Tooltip("Refuse to RECORD when the rate is unpinned. Your GetInput() is called once per " +
                 "Update and once per FixedUpdate, ButonManager.GetTapX() is consumed by it, and " +
                 "xvel/total_frame accumulate per step - so an unpinned session records a run whose " +
                 "input resolution depended on the frame rate at the time. Better to refuse than to " +
                 "hand out a tape that can never replay.")]
        public bool refuseRecordWhenRateUnpinned = true;
        public float speed = 1f;                 // 0.03125 .. 8  (playback only)
        public int maxTicks = 216000;            // 1 h at 60 Hz
        public int checkpointEvery = 60;         // 1 s of state for correction + seeking

        [Header("Savestates")]
        public int ringCapacity = 1800;          // per-tick states kept in RAM (30 s of rollback)
        public bool snapshotEveryTick = true;    // what makes rollback feel instant
        public int slotCount = 10;               // named on-disk slots (1..9,0)
        public bool resetVisualsOnLoad = true;   // particles/decals/trails are not sim, but they look wrong

        [Header("Branching")]
        public bool keepDiscardedTailAsBranch = true;   // rollback never destroys an attempt
        public int maxBranches = 24;

        [Header("Keys (PC)")]
        public KeyCode keyPanel = KeyCode.F9;
        public KeyCode keyRecord = KeyCode.F10;
        public KeyCode keyStop = KeyCode.F11;
        public KeyCode keyReplay = KeyCode.F12;
        public KeyCode keyRollbackLast = KeyCode.Backspace;
        public KeyCode keyStepFwd = KeyCode.Period;
        public KeyCode keyStepBack = KeyCode.Comma;
        public KeyCode keySlowDown = KeyCode.Minus;
        public KeyCode keySlowUp = KeyCode.Equals;
        public KeyCode keySlowReset = KeyCode.LeftBracket;
        public KeyCode keyPause = KeyCode.RightBracket;
        public KeyCode keySlotSave = KeyCode.Alpha0;      // 1..9,0 => slot 0..9
        public KeyCode keySlotLoadModifier = KeyCode.LeftShift;
        public KeyCode keyQuickSave = KeyCode.F8;          // Ctrl held => quick load

        // ---- persistence ----
        public static string Dir
        {
            get
            {
                string d = Path.Combine(Application.persistentDataPath, "tas");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string ConfigPath { get { return Path.Combine(Dir, "config.json"); } }
        public static string LibraryPath { get { return Path.Combine(Dir, "library.json"); } }
        public static string LastRunPath { get { return Path.Combine(Dir, "last_run.txt"); } }

        static TasToolConfig cached;

        public static TasToolConfig Load()
        {
            if (cached != null) return cached;
            try
            {
                if (File.Exists(ConfigPath))
                    cached = JsonUtility.FromJson<TasToolConfig>(File.ReadAllText(ConfigPath)) ?? new TasToolConfig();
                else cached = new TasToolConfig();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TAS] config load failed, using defaults: " + e.Message);
                cached = new TasToolConfig();
            }
            cached.Sanitize();
            return cached;
        }

        public void Save()
        {
            Sanitize();
            cached = this;
            try { File.WriteAllText(ConfigPath, JsonUtility.ToJson(this, true)); }
            catch (Exception e) { Debug.LogWarning("[TAS] config save failed: " + e.Message); }
        }

        public void Sanitize()
        {
            tickRate = Mathf.Clamp(tickRate, 20, 240);
            speed = Mathf.Clamp(speed, 0.03125f, 8f);
            maxTicks = Mathf.Clamp(maxTicks, 600, 60 * 60 * 240);
            checkpointEvery = Mathf.Clamp(checkpointEvery, 10, 600);
            ringCapacity = Mathf.Clamp(ringCapacity, 60, 20000);
            slotCount = Mathf.Clamp(slotCount, 1, 32);
            maxBranches = Mathf.Clamp(maxBranches, 1, 256);
        }

        public static string HumanSpeed(float s)
        {
            if (s >= 1f) return s.ToString("0.###") + "x";
            return "1/" + Mathf.Max(1, Mathf.RoundToInt(1f / s)) + "  (" + s.ToString("0.###") + "x)";
        }
    }
}
