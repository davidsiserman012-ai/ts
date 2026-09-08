using System.IO;
using UnityEngine;

namespace Tas
{
    /// <summary>
    /// The tool panel *inside the build* (not the editor window), because a TAS run is made by
    /// playing the game: you want savestates and slow-mo while you are in the level, and you want
    /// them in the same binary you verified. IMGUI on purpose - zero dependencies, works in a
    /// player, and a dev overlay does not need to be pretty.
    ///
    /// Gated by TasToolConfig.toolEnabled + panelVisible (F9). Ship with toolEnabled=false and the
    /// whole thing is inert and invisible; there is no code path in the recorder or playback that a
    /// release client can be tricked into driving, because the UI never subscribes and the clock
    /// never enters manual mode.
    /// </summary>
    [DefaultExecutionOrder(32002)]
    public sealed class TasToolUI : MonoBehaviour
    {
        public static TasToolUI i;
        public float width = 470f;
        public float top = 120f;
        Vector2 scroll;
        int jumpTick;
        int speedIdx;
        bool showBranches = true;
        GUIStyle h, s, btn;
        Texture2D panel, accent;
        bool styles;

        static readonly float[] Speeds = { 1f / 32f, 1f / 16f, 1f / 8f, 1f / 4f, 1f / 2f, 1f, 2f, 4f, 8f };

        void Awake() { i = this; }

        void Build()
        {
            styles = true;
            panel = Tex(new Color(0.055f, 0.06f, 0.08f, 0.94f));
            accent = Tex(new Color(0.30f, 0.66f, 1f, 1f));
            h = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            s = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.82f, 0.86f, 0.92f) } };
            btn = new GUIStyle(GUI.skin.button) { fontSize = 11, padding = { top = 2, bottom = 2 } };
        }

        static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(2, 2);
            Color[] a = new Color[4];
            for (int k = 0; k < 4; k++) a[k] = c;
            t.SetPixels(a); t.Apply();
            return t;
        }


        TasToolConfig cfg { get { return TasTool.i != null ? TasTool.i.cfg : TasToolConfig.Load(); } }

        void OnGUI()
        {
            TasTool tool = TasTool.i;
            if (tool == null || !tool.cfg.toolEnabled || !tool.cfg.panelVisible) return;
            if (!styles) Build();

            TasToolConfig c = tool.cfg;
            GUILayout.BeginArea(new Rect(12f, top, width, Screen.height - top - 16f), panel, GUI.skin.box);
            GUILayout.Label("NATIVE TAS TOOL", h);
            GUILayout.Label(tool.DebugLine, s);
            GUILayout.Space(4);

            // ---- the two toggles that define the workflow ----
            GUILayout.BeginHorizontal();
            bool rec = GUILayout.Toggle(c.autoRecordOnGameStart, "auto-record on game start", btn);
            bool rep = GUILayout.Toggle(c.autoReplayOnGameStart, "REPLAY on game start", btn);
            GUILayout.EndHorizontal();
            if (rec != c.autoRecordOnGameStart) { c.autoRecordOnGameStart = rec; c.Save(); }
            if (rep != c.autoReplayOnGameStart) tool.ToggleReplay();

            GUILayout.BeginHorizontal();
            c.loopReplay = GUILayout.Toggle(c.loopReplay, "loop replay", btn);
            c.enforceFixedRate = GUILayout.Toggle(c.enforceFixedRate, "lock 1 tick = 1 frame", btn);
            c.snapshotEveryTick = GUILayout.Toggle(c.snapshotEveryTick, "savestate every tick", btn);
            GUILayout.EndHorizontal();
            if (GUI.changed)
            {
                c.Save();
                if (tool.Clock != null) { tool.Clock.enforceFixedRate = c.enforceFixedRate; tool.Clock.SetTickRate(c.tickRate); }
            }

            GUILayout.Space(6);
            GUILayout.Label("TRANSPORT", h);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(c.autoReplayOnGameStart ? "Play replay now" : "Play tape", btn, GUILayout.Width(96)))
                tool.PlayNow(jumpTick);
            if (GUILayout.Button("Stop", btn, GUILayout.Width(48))) tool.StopTransport();
            if (GUILayout.Button("Record", btn, GUILayout.Width(56))) tool.ToggleRecord();
            if (GUILayout.Button("Save run", btn, GUILayout.Width(62))) tool.SaveRun();
            if (GUILayout.Button("Export demo json", btn, GUILayout.Width(118)))
            {
                if (tool.Recorder != null && tool.Recorder.Tape != null)
                    File.WriteAllText(System.IO.Path.Combine(TasToolConfig.Dir, "last_demo.json"),
                                      TasDemoExport.ToJson(tool.Recorder.Tape));
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("speed", s, GUILayout.Width(34));
            int si = speedIdx;
            for (int k = 0; k < Speeds.Length; k++)
                if (Mathf.Abs(Speeds[k] - c.speed) < 0.001f) { si = k; break; }
            si = GUILayout.SelectionGrid(si, SpeedLabels(), 5, btn);
            if (si != speedIdx || !Mathf.Approximately(Speeds[si], c.speed)) { speedIdx = si; tool.SetSpeed(Speeds[si]); }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("-1 tick", btn)) tool.Step(-1);
            if (GUILayout.Button("+1 tick", btn)) tool.Step(1);
            tool.Clock.paused = GUILayout.Toggle(tool.Clock.paused, "paused", btn);
            if (GUILayout.Button("rollback 1s", btn)) tool.RollbackTicks(c.tickRate);
            if (GUILayout.Button("rollback 5s", btn)) tool.RollbackTicks(c.tickRate * 5);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("tick", s, GUILayout.Width(26));
            jumpTick = Mathf.Clamp(int.Parse(Fold(GUILayout.TextField(jumpTick.ToString(), GUILayout.Width(58)))), 0, 1 << 30);
            if (GUILayout.Button("seek", btn)) tool.RollbackTo(jumpTick);
            GUILayout.Label("frames: " + (tool.Recorder != null ? tool.Recorder.TickCount : 0) +
                            "   t=" + (tool.Recorder != null ? tool.Recorder.RunSeconds.ToString("0.000") : "0") + "s", s);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("SAVESTATES  (digit = save, " + c.keySlotLoadModifier + "+digit = load)", h);
            GUILayout.BeginHorizontal();
            for (int k = 0; k < Mathf.Min(10, c.slotCount); k++)
            {
                TasSlotInfo info = TasSavestates.SlotInfo(k);
                string cap = info == null ? (k == 9 ? "0" : (k + 1).ToString()) :
                             (k == 9 ? "0" : (k + 1).ToString()) + "\n" + info.tick + "t\n" + info.simTime.ToString("0.0") + "s";
                GUILayout.BeginVertical(GUILayout.Width(42));
                if (GUILayout.Button(cap, btn, GUILayout.Height(42))) tool.SaveSlot(k);
                if (GUILayout.Button("load", btn)) tool.LoadSlot(k);
                GUILayout.EndVertical();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("ring: " + TasSavestates.RingCount + "/" + c.ringCapacity + " ticks   " +
                            (TasSavestates.EstimatedRamBytes / 1024) + " KB   newest tick " + TasSavestates.RingNewestTick +
                            "   snap=" + TasSavestates.LastSnapshotBytes + "B" +
                            (TasSavestates.OverflowCount > 0 ? "  OVERFLOW x" + TasSavestates.OverflowCount : ""), s);

            GUILayout.Space(6);
            showBranches = GUILayout.Toggle(showBranches, "BRANCHES (rewound attempts)", h);
            if (showBranches)
            {
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(120));
                var bs = tool.branches.Branches;
                if (bs.Count == 0) GUILayout.Label("none yet - each rollback stashes the tail it removes", s);
                for (int k = bs.Count - 1; k >= 0; k--)
                {
                    TasBranch b = bs[k];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("#" + b.id + " " + b.label + "  @" + b.parentTick + "  " +
                                    b.frameCount + "t  " + b.durationSeconds.ToString("0.000") + "s" +
                                    (b.discarded ? " (superseded)" : ""), s, GUILayout.Width(width - 150));
                    if (GUILayout.Button("splice", btn, GUILayout.Width(52))) tool.branches.Splice(b.id);
                    if (GUILayout.Button("x", btn, GUILayout.Width(22))) tool.branches.Drop(b.id);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                GUILayout.Label("splice = trunk[cut..] := branch. Trunk stays one contiguous range, so replay is one run.", s);
            }

            GUILayout.Space(4);
            GUILayout.Label("panel: " + c.keyPanel + "   record: " + c.keyRecord + "   stop: " + c.keyStop +
                            "   replay toggle: " + c.keyReplay + "   slow: " + c.keySlowDown + "/" + c.keySlowUp +
                            "   reset: " + c.keySlowReset, s);
            GUILayout.EndArea();
        }

        string[] SpeedLabels()
        {
            string[] o = new string[Speeds.Length];
            for (int k = 0; k < o.Length; k++) o[k] = TasToolConfig.HumanSpeed(Speeds[k]);
            return o;
        }

        static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "0";
            int v;
            return int.TryParse(s, out v) ? v.ToString() : "0";
        }
    }
}
