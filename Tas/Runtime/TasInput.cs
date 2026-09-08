using UnityEngine;

namespace Tas
{
    /// <summary>
    /// THE seam that makes a TAS tool possible. Every gameplay read of player input goes
    /// through TasInput.Current. In record mode it is filled from the live device; in
    /// playback mode it is filled from the tape. The game logic cannot tell the difference,
    /// which is exactly the property you need.
    ///
    /// Your old recorder had this hook and threw it away: GetInputData() built a
    /// List&lt;Vector2Int&gt; of touch positions and was never called by anything, and
    /// record_touches was never read. That list, moved into the tick loop and quantized,
    /// is the tape.
    /// </summary>
    public static class TasInput
    {
        public static TasInputFrame Current;     // what the sim already consumed this tick
        public static TasInputFrame Pending;     // what will be consumed next tick

        /// <summary>Live device reader. Null during pure playback / headless verify.</summary>
        public static System.Func<TasInputFrame> LiveReader;

        public static bool SourcedFromTape;
        public static bool BlockLiveInput;       // true => player cannot touch the sim (playback/spectate)

        /// <summary>Called by TasClock at OnTickHead, before any sim code reads input.</summary>
        public static void Commit()
        {
            if (SourcedFromTape)
            {
                Current = Pending;
                return;
            }
            if (LiveReader == null) { Current = default(TasInputFrame); return; }
            Current = LiveReader();
            Pending = Current;                   // keep the two views consistent for UI
        }

        public static void PushFromTape(TasInputFrame f)
        {
            Pending = f;
            SourcedFromTape = true;
        }

        public static void ReleaseToLive()
        {
            SourcedFromTape = false;
        }

        // ---- convenience readers for patched gameplay code ----------------
        public static bool GetButton(TasButton b) { return Current.Has(b); }
        public static bool GetButtonDown(TasButton b) { return Current.Has(b) && !Previous.Has(b); }
        public static bool GetButtonUp(TasButton b) { return !Current.Has(b) && Previous.Has(b); }
        public static Vector2 MoveAxis() { return Current.Move; }
        public static Vector2 LookDelta() { return Current.Look; }

        public static TasInputFrame Previous;

        /// <summary>Called by TasClock at OnTickTail.</summary>
        public static void Latch() { Previous = Current; }
    }

    /// <summary>
    /// Default live reader for a UnityStandardAssets-style first person controller on touch.
    /// ⚠ ADAPT THE AXIS / BUTTON NAMES TO YOUR PROJECT. This class is the only place that
    /// touches UnityEngine.Input; everything downstream is platform-agnostic.
    /// </summary>
    public static class TasLiveInput
    {
        public static float lookSensitivity = 1.0f;
        public static Vector2 lastAimScreenPos;
        public static bool aimHeld;

        public static void Install()
        {
            TasInput.LiveReader = Read;
        }

        public static TasInputFrame Read()
        {
            TasInputFrame f = new TasInputFrame();

            Vector2 move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            f.moveX = TasInputFrame.FromFloat(Mathf.Clamp(move.x, -1f, 1f));
            f.moveY = TasInputFrame.FromFloat(Mathf.Clamp(move.y, -1f, 1f));

            f.Set(TasButton.Jump, Input.GetButton("Jump"));
            f.Set(TasButton.JumpPressed, Input.GetButtonDown("Jump"));
            f.Set(TasButton.Fire, Input.GetMouseButton(0));
            f.Set(TasButton.FirePressed, Input.GetMouseButtonDown(0));
            f.Set(TasButton.AltFire, Input.GetMouseButton(1));
            f.Set(TasButton.Crouch, Input.GetKey(KeyCode.C));
            f.Set(TasButton.Sprint, Input.GetKey(KeyCode.LeftShift));
            f.Set(TasButton.Walk, Input.GetKey(KeyCode.LeftControl));
            f.Set(TasButton.Reload, Input.GetKey(KeyCode.R));
            f.Set(TasButton.Use, Input.GetKey(KeyCode.E));

            // ---- touch: look is a *delta*, so quantize the delta, not the position ----
            Vector2 look = Vector2.zero;
            int tc = Input.touchCount;
            f.pointerCount = (byte)Mathf.Min(255, tc);

            for (int k = 0; k < tc; k++)
            {
                Touch t = Input.GetTouch(k);
                bool lookZone = t.position.x > Screen.width * 0.5f;   // ⚠ your layout's split
                if (lookZone)
                {
                    look += t.deltaPosition * lookSensitivity;
                }
                else
                {
                    // left half drives the stick: recompute move from the finger offset
                    Vector2 dir = (t.position - new Vector2(Screen.width * 0.25f, Screen.height * 0.25f));
                    dir /= Mathf.Max(1f, Screen.height * 0.18f);
                    f.moveX = TasInputFrame.FromFloat(Mathf.Clamp(dir.x, -1f, 1f));
                    f.moveY = TasInputFrame.FromFloat(Mathf.Clamp(dir.y, -1f, 1f));
                }

                if (t.phase == TouchPhase.Began || t.phase == TouchPhase.Stationary ||
                    t.phase == TouchPhase.Moved || t.phase == TouchPhase.Ended)
                {
                    f.Set(TasButton.Fire, lookZone);   // ⚠ replace with your fire-button hit test
                }
            }

            f.lookX = TasInputFrame.FromDeg(look.x);
            f.lookY = TasInputFrame.FromDeg(-look.y);

            // normalized aim pointer -> resolution independent, so DemoData.screenWidth/Height
            // is no longer needed to reconstruct the run
            Vector2 aim = new Vector2(
                lastAimScreenPos.x / Mathf.Max(1, Screen.width),
                lastAimScreenPos.y / Mathf.Max(1, Screen.height));
            if (aimHeld)
            {
                f.aimX = TasInputFrame.FromNorm(aim.x);
                f.aimY = TasInputFrame.FromNorm(aim.y);
            }
            else
            {
                f.aimX = TasInputFrame.PointerAbsent;
                f.aimY = TasInputFrame.PointerAbsent;
            }

            return f;
        }
    }
}
