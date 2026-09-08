using UnityEngine;

namespace Tas
{
    /// <summary>
    /// On-screen TAS readout (the hotkeys live in TasTool now - one owner for keys, or you get
    /// double toggles). The input display is not decoration: it is how a
    /// viewer tells a real run from a hand-written tape, and it is the fastest way for you to
    /// see that a recorded tick and a replayed tick differ.
    /// </summary>
    [DefaultExecutionOrder(32001)]
    public sealed class TasOverlay : MonoBehaviour
    {
        public static TasOverlay i;
        public bool showInputBox = true;
        public bool showClock = true;
        public bool hotkeys = true;
        public int boxSize = 190;

        GUIStyle lbl, big, warn;
        Texture2D bg, hot, cold, ring;
        bool styles;

        void Awake() { i = this; }

        void Build()
        {
            styles = true;
            bg = MakeTex(new Color(0f, 0f, 0f, 0.62f));
            hot = MakeTex(new Color(0.20f, 0.85f, 0.45f, 0.95f));
            cold = MakeTex(new Color(1f, 0.28f, 0.22f, 0.95f));
            ring = MakeTex(new Color(1f, 1f, 1f, 0.13f));
            lbl = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = Color.white } };
            big = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            warn = new GUIStyle(lbl) { normal = { textColor = new Color(1f, 0.55f, 0.4f) }, fontStyle = FontStyle.Bold };
        }

        static Texture2D MakeTex(Color c)
        {
            Texture2D t = new Texture2D(2, 2);
            Color[] a = new Color[4];
            for (int k = 0; k < 4; k++) a[k] = c;
            t.SetPixels(a); t.Apply();
            return t;
        }

        public void Toggle()
        {
            if (!TasClock.Exists) return;
            TasClock.i.paused = !TasClock.i.paused;
        }

        void OnGUI()
        {
            if (!styles) Build();

            int tick = (int)(TasClock.Exists ? TasClock.Tick : 0);
            double secs = TasClock.Exists ? TasClock.i.simTime : 0.0;

            if (showClock)
            {
                GUI.DrawTexture(new Rect(10, 10, 300, 92), bg);
                GUI.Label(new Rect(20, 14, 280, 26), (secs).ToString("0.000"), big);
                GUI.Label(new Rect(20, 42, 280, 18), "tick " + tick +
                          "   " + (TasClock.Exists ? TasClock.i.tickRate : 0) + "Hz   x" +
                          (TasClock.Exists ? TasClock.i.speed.ToString("0.###") : "1"), lbl);
                string st = TasRecorder.i != null && TasRecorder.i.recording ? "REC" :
                            TasPlayback.i != null && TasPlayback.i.playing ? "PLAY" : "idle";
                GUI.Label(new Rect(20, 60, 280, 18), st +
                          (TasClock.Exists && TasClock.i.paused ? " (paused)" : ""),
                          st == "idle" ? lbl : warn);

                if (TasPlayback.i != null && (TasPlayback.i.divergences > 0 || TasPlayback.i.corrections > 0))
                    GUI.Label(new Rect(20, 78, 620, 18), "desync " + TasPlayback.i.divergences +
                              "  fixed " + TasPlayback.i.corrections +
                              "  err " + TasPlayback.i.lastError.ToString("0.00") + "m", warn);

                // The two signals that mean "this replay is not your run" for a reason physics cannot
                // see: input arriving off-tape, and the cursor pin slipping. Kept off the desync line on
                // purpose - a desync says the sim moved, these say the *inputs* did not come from the tape.
                if (TasPlayback.i != null && (TasPlayback.i.inputIntegrityErrors > 0 || TasPlayback.i.cursorMismatchTicks > 0))
                    GUI.Label(new Rect(20, 96, 620, 18),
                              "INPUT NOT IN TAPE " + TasPlayback.i.inputIntegrityErrors +
                              "  CURSOR " + TasPlayback.i.cursorMismatchTicks + " - replay != run", warn);
            }

            if (!showInputBox) return;

            TasInputFrame f = TasInput.Current;
            if (TasPlayback.i != null && TasPlayback.i.playing) f = TasPlayback.i.CurrentFrame();

            float x = Screen.width - boxSize - 12f, y = Screen.height - boxSize - 12f;
            GUI.DrawTexture(new Rect(x, y, boxSize, boxSize), bg);

            // stick
            Vector2 c = new Vector2(x + boxSize * 0.28f, y + boxSize * 0.62f);
            GUI.Label(new Rect(x + 8, y + 4, boxSize, 16), "MOVE", lbl);
            GUI.DrawTexture(new Rect(c.x - 26, c.y - 26, 52, 52), ring);
            Vector2 m = f.Move;
            GUI.DrawTexture(new Rect(c.x + m.x * 22f - 4, c.y - m.y * 22f - 4, 8, 8), hot);

            // aim
            GUI.Label(new Rect(x + boxSize * 0.58f, y + 4, boxSize, 16), "AIM", lbl);
            Vector2 a = f.AimNorm;
            if (a.x >= 0f)
                GUI.DrawTexture(new Rect(x + boxSize * 0.6f + a.x * (boxSize * 0.34f) - 3,
                                         y + 24 + (1f - a.y) * (boxSize * 0.5f) - 3, 6, 6), hot);

            // buttons
            float by = y + boxSize - 46f;
            string[] names = { "JMP", "FIRE", "RLD", "CRCH", "SPRT" };
            TasButton[] bs = { TasButton.Jump, TasButton.Fire, TasButton.Reload, TasButton.Crouch, TasButton.Sprint };
            for (int k = 0; k < names.Length; k++)
            {
                bool on = f.Has(bs[k]);
                Rect r = new Rect(x + 8 + k * 36f, by, 32, 16);
                GUI.DrawTexture(r, on ? hot : cold);
                GUI.Label(new Rect(r.x, r.y, r.width, 16), names[k], lbl);
            }
            GUI.Label(new Rect(x + 8, by + 20, boxSize - 16, 16), "look " + f.Look.x.ToString("0.0") + "," +
                      f.Look.y.ToString("0.0"), lbl);
        }
    }
}
