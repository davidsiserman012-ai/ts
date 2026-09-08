using System.Collections.Generic;
using System.IO;
using Tas;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The native TAS tool surface. Editor-side on purpose: recording/verification must run in
/// the *game build* (so it ships to players), but authoring, scrubbing and frame editing are
/// dev-only, and keeping them in Editor only means nothing in a release build can be driven by
/// a tampered tape UI. Runtime hotkeys (F9/F10/F11, numpad) cover the in-game half.
/// </summary>
public sealed class TasToolWindow : EditorWindow
{
    TasTape tape;
    string path;
    Vector2 scroll;
    bool dirty;
    int jumpTick;
    float speed = 1f;
    bool pausePlayback;
    TasPlaybackMode mode = TasPlaybackMode.ResimLive;
    string report = "";

    static readonly string[] BtnNames = { "Jump", "Fire", "AltFire", "Reload", "Use", "Crouch", "Walk", "Sprint" };
    static readonly TasButton[] Btns =
    {
        TasButton.Jump, TasButton.Fire, TasButton.AltFire, TasButton.Reload,
        TasButton.Use, TasButton.Crouch, TasButton.Walk, TasButton.Sprint
    };

    [MenuItem("Window/TAS Tool")]
    public static void Open() { GetWindow<TasToolWindow>("TAS Tool"); }

    void OnEnable()
    {
        EditorApplication.update += Repaint;
        if (TasRecorder.i != null && TasRecorder.i.recording) report = "Play-mode recorder is live.";
    }

    void OnDisable() { EditorApplication.update -= Repaint; }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Session", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Take tape from recorder")) AdoptFromRecorder();
            if (GUILayout.Button("Open .tas")) OpenPanel();
            if (GUILayout.Button("Save")) Save();
            if (GUILayout.Button("Save As")) SaveAsPanel();
        }
        EditorGUILayout.LabelField("File", path == null ? "-" : Path.GetFileName(path));
        if (tape != null)
            EditorGUILayout.LabelField("Header", tape.Summary());

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transport", EditorStyles.boldLabel);
        bool play = EditorApplication.isPlaying;
        using (new EditorGUI.DisabledScope(!play || tape == null))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Play")) TasPlayback.i.Play(tape, jumpTick, mode);
            if (GUILayout.Button("Stop")) { if (TasPlayback.i != null) TasPlayback.i.Stop(); }
            if (GUILayout.Button("Frame -")) { if (TasPlayback.i != null) TasPlayback.i.StepForward(-1); }
            if (GUILayout.Button("Frame +")) { if (TasPlayback.i != null) TasPlayback.i.StepForward(1); }
        }
        mode = (TasPlaybackMode)EditorGUILayout.EnumPopup("Mode", mode);
        speed = EditorGUILayout.Slider("Speed", speed, 0f, 8f);
        jumpTick = EditorGUILayout.IntField("Tick", jumpTick);
        if (tape != null) jumpTick = Mathf.Clamp(jumpTick, 0, Mathf.Max(0, tape.frames.Count - 1));
        using (new EditorGUI.DisabledScope(!play))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Seek")) { if (TasPlayback.i != null) { TasPlayback.i.Load(tape); TasPlayback.i.SeekTo(jumpTick); } }
            pausePlayback = GUILayout.Toggle(pausePlayback, "Paused");
            if (GUILayout.Button("Apply speed/pause") && TasPlayback.i != null)
            {
                TasPlayback.i.SetSpeed(speed);
                TasPlayback.i.SetPaused(pausePlayback);
            }
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Keyframe here")) TasSnapshot.PutAtCurrentTick();
            if (GUILayout.Button("Rollback to keyframe")) Report(TasSnapshot.RestoreIfAvailable(jumpTick) ? "rolled back" : "no keyframe at tick");
            if (GUILayout.Button("Verify tape")) Verify();
            if (GUILayout.Button("Export leaderboard JSON")) ExportDemo();
        }
        EditorGUILayout.LabelField("Keyframes cached", TasSnapshot.Count.ToString());

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Input grid (editable)", EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        if (tape != null)
        {
            int from = Mathf.Max(0, jumpTick - 2), to = Mathf.Min(tape.frames.Count, from + 64);
            for (int k = from; k < to; k++) Row(k);
            if (tape.frames.Count > to) EditorGUILayout.LabelField("... " + (tape.frames.Count - to) + " more ticks (move Tick field to page)");
        }
        else EditorGUILayout.LabelField("No tape loaded.");
        EditorGUILayout.EndScrollView();

        if (dirty && GUILayout.Button("Commit edits")) Commit();
        if (!string.IsNullOrEmpty(report)) EditorGUILayout.HelpBox(report, MessageType.Info);
    }

    void Row(int k)
    {
        TasInputFrame f = tape.frames[k];
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(k.ToString(), GUILayout.Width(46));
            for (int b = 0; b < Btns.Length; b++)
            {
                bool on = f.Has(Btns[b]);
                bool n = GUILayout.Toggle(on, BtnNames[b], "Button", GUILayout.Width(52));
                if (n != on) { f.Set(Btns[b], n); dirty = true; }
            }
            EditorGUILayout.LabelField("mv", GUILayout.Width(18));
            float mx = EditorGUILayout.FloatField(f.MoveXf, GUILayout.Width(42));
            float my = EditorGUILayout.FloatField(f.MoveYf, GUILayout.Width(42));
            if (!Mathf.Approximately(mx * TasInputFrame.FP, f.moveX) || !Mathf.Approximately(my * TasInputFrame.FP, f.moveY))
            { f.moveX = (short)Mathf.RoundToInt(mx * TasInputFrame.FP); f.moveY = (short)Mathf.RoundToInt(my * TasInputFrame.FP); dirty = true; }
            EditorGUILayout.LabelField("look", GUILayout.Width(28));
            float lx = EditorGUILayout.FloatField(f.LookXf, GUILayout.Width(48));
            float ly = EditorGUILayout.FloatField(f.LookYf, GUILayout.Width(48));
            if (!Mathf.Approximately(lx, f.LookXf) || !Mathf.Approximately(ly, f.LookYf))
            { f.lookX = TasInputFrame.FromDeg(lx); f.lookY = TasInputFrame.FromDeg(ly); dirty = true; }
            if ((k % Mathf.Max(1, tape.header.checkpointEvery)) == 0)
                EditorGUILayout.LabelField("KF", GUILayout.Width(22));
            tape.frames[k] = f;
        }
    }

    void Commit()
    {
        for (int k = 0; k < tape.frames.Count; k++)
        {
            TasInputFrame f = tape.frames[k];
            TasInputFrame p = k > 0 ? tape.frames[k - 1] : default(TasInputFrame);
            f.Set(TasButton.JumpPressed, f.Has(TasButton.Jump) && !p.Has(TasButton.Jump));
            f.Set(TasButton.FirePressed, f.Has(TasButton.Fire) && !p.Has(TasButton.Fire));
            if (f.Has(TasButton.Jump) != p.Has(TasButton.Jump) || f.Has(TasButton.Fire) != p.Has(TasButton.Fire) ||
                f.moveX != p.moveX || f.moveY != p.moveY)
                f.flags |= (byte)TasFrameFlags.Teleport;      // edited frame: not a live capture
            tape.frames[k] = f;
        }
        tape.BuildIndex();
        dirty = false;
        Report("edits committed, " + tape.frames.Count + " ticks");
    }

    void AdoptFromRecorder()
    {
        if (TasRecorder.i == null || TasRecorder.i.Tape == null) { Report("no recorder tape in this Play session"); return; }
        tape = TasRecorder.i.Tape;
        path = null; dirty = true;
        Report("adopted live tape (still recording - edits will be overwritten)");
    }

    void OpenPanel()
    {
        string dir = string.IsNullOrEmpty(Application.streamingAssetsPath) ? "." : Application.dataPath;
        string f = EditorUtility.OpenFilePanel("Open tape", dir, "tas");
        if (!string.IsNullOrEmpty(f)) LoadFile(f);
    }

    void SaveAsPanel()
    {
        string f = EditorUtility.SaveFilePanel("Save tape", ".", "run", "tas");
        if (!string.IsNullOrEmpty(f)) { path = f; Save(); }
    }

    public void LoadFile(string f)
    {
        try { tape = TasTape.Read(f); path = f; dirty = false; jumpTick = 0; Report("loaded " + tape.Summary()); }
        catch (System.Exception e) { Report("load failed: " + e.Message); }
    }

    void Save()
    {
        if (tape == null || string.IsNullOrEmpty(path)) { Report("nothing to save / no path"); return; }
        TasTape.Write(path, tape);
        dirty = false;
        Report("saved " + new FileInfo(path).Length + " bytes");
    }

    /// <summary>
        /// Cheap structural audit, all of it doable server-side with no Unity on the box:
        /// this is the list you can run as a filter before you spend a verify slot.
        /// </summary>
    void Verify()
    {
        if (tape == null) { Report("no tape"); return; }
        List<string> bad = new List<string>();
        if (tape.header.tickCount != tape.frames.Count) bad.Add("header.tickCount != frames");
        if (tape.header.tickRate < 10 || tape.header.tickRate > 1000) bad.Add("implausible tickRate");
        if (tape.header.configHash != TasRecorder.ComputeConfigHash()) bad.Add("configHash != current build");
        if (TasTape.FramesHash(tape.frames) == 0u) bad.Add("null frame hash");

        float maxSpd = 0f;
        for (int k = 1; k < tape.frames.Count; k++)
        {
            TasInputFrame f = tape.frames[k];
            if (Mathf.Abs(f.MoveXf) > 1.001f || Mathf.Abs(f.MoveYf) > 1.001f) { bad.Add("axis out of range at " + k); break; }
        }
        for (int k = 0; k < tape.checkpoints.Count; k++)
        {
            TasCheckpoint c = tape.checkpoints[k];
            if (k > 0)
            {
                TasCheckpoint p = tape.checkpoints[k - 1];
                float dt = (c.tick - p.tick) / (float)Mathf.Max(1, tape.header.tickRate);
                float sp = Vector3.Distance(c.Position, p.Position) / Mathf.Max(0.0001f, dt);
                if (sp > maxSpd) maxSpd = sp;
            }
            if (float.IsNaN(c.px) || float.IsInfinity(c.px)) { bad.Add("NaN position at tick " + c.tick); break; }
        }

        // 12 m/s is a placeholder for "faster than the fastest legitimate movement source".
        // Put your real number here (s+b + bunnyhop + ramp/boost max) and keep it in config,
        // because the server must be able to run the same check.
        if (maxSpd > 12f) bad.Add("checkpoint speed spike " + maxSpd.ToString("0.0") + " m/s");

        Report(bad.Count == 0
            ? "structure OK. Now the real check: mode=ResimVerify, Play, and 0 divergences."
            : "SUSPECT:\n - " + string.Join("\n - ", bad.ToArray()));
    }

    void ExportDemo()
    {
        if (tape == null) { Report("no tape"); return; }
        string json = TasDemoExport.ToJson(tape);
        string p = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path ?? "run") + "_demo.json");
        File.WriteAllText(p, json);
        Report("demo json -> " + p + "  (" + new FileInfo(p).Length + " B) | tape gz+b64 " +
               TasDemoExport.ToWire(tape).Length + " B");
    }

    void Report(string s) { report = s; Repaint(); }
}
