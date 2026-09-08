using UnityEngine;

namespace Tas
{
    /// <summary>
    /// Default live reader for a PC build: keyboard + mouse, old Input Manager.
    ///
    /// The important rule: record what the SIM CONSUMED, not what the OS delivered.
    /// Unity's Input.GetAxis("Mouse X") is a *filtered* axis - it carries per-frame smoothing
    /// state (sensitivity/gravity/dead zone) inside the Input Manager. If you record the raw
    /// device delta and the controller then re-filters it during playback, the filter state at
    /// tick 4213 is not the filter state it had when you played, and the aim will drift.
    ///
    /// So TasLiveInput prefers the injected readers (LookReader / MoveReader), which are meant
    /// to be wired to the values your RigidbodyFirstPersonController actually applied:
    ///
    ///     TasLiveInput.MoveReader = () => new Vector2(m_HorizontalInput, m_VerticalInput);
    ///     TasLiveInput.LookReader = () => m_MouseLook.CalculateMouseDelta();   // degrees
    ///
    /// The fallback path below is "good enough to start" and is exactly what the standard
    /// asset's MouseLook computes from - but check it against your file, and if your controller
    /// uses GetAxis (smoothed) rather than GetAxisRaw, wire the reader instead of using it.
    /// </summary>
    public static class TasLiveInput
    {
        public static float lookSensitivity = 1.0f;       // part of configHash on purpose
        public static float mouseDegreesPerPixel = 1.0f;  // ⚠ match MouseLook.m_MouseSenseX
        public static System.Func<Vector2> MoveReader;
        public static System.Func<Vector2> LookReader;
        public static System.Func<uint> ButtonReader;     // optional: your own action map -> bits

        static Vector3 lastMouse;
        static bool haveLast;
        static CursorLockMode lastLock = CursorLockMode.None;

        /// <summary>Set by TasInputAdapter: the game's own sampler wins over this file's fallback.</summary>
        public static System.Func<TasInputFrame> SampleOverride;

        public static void Install()
        {
            TasInput.LiveReader = Read;
            lastMouse = Input.mousePosition;
            haveLast = true;
            lastLock = Cursor.lockState;
        }

        public static TasInputFrame Read()
        {
            if (SampleOverride != null)
            {
                TasInputFrame g = SampleOverride();
                if (Cursor.lockState != lastLock) { lastLock = Cursor.lockState; ResyncCursor(); }
                return g;
            }
            return ReadFallback();
        }

        /// <summary>
        /// Buttons only, and PURE: no cursor resync, no MouseDelta(), because MouseDelta advances
        /// lastMouse/haveLast and ResyncCursor writes state - an input *probe* that consumes the mouse
        /// delta would take the delta away from the capture that is happening in the same frame, and the
        /// run would be recorded with zero look for no visible reason. This is the reader a checker
        /// should use; anything that needs look/aux must go through Read(), once, as the capture.
        /// </summary>
        public static uint PeekButtons()
        {
            if (ButtonReader != null) return ButtonReader();
            TasInputFrame f = new TasInputFrame();
            ReadDefaultButtons(ref f);
            return f.buttons;
        }

        public static TasInputFrame ReadFallback()
        {
            TasInputFrame f = new TasInputFrame();

            // GameManager.Olay_OyuncuDustu / Olay_LevelTamamlandi do `Cursor.lockState = None`, and
            // StartGame locks it again. Crossing either boundary without a resync records the cursor's
            // jump back to centre as one enormous look delta - which then replays as a 180 degree snap.
            if (Cursor.lockState != lastLock)
            {
                lastLock = Cursor.lockState;
                ResyncCursor();
            }

            Vector2 move = MoveReader != null ? MoveReader()
                                               : new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            f.moveX = TasInputFrame.FromFloat(Mathf.Clamp(move.x, -1f, 1f));
            f.moveY = TasInputFrame.FromFloat(Mathf.Clamp(move.y, -1f, 1f));

            Vector2 look = LookReader != null ? LookReader() : MouseDelta();
            f.lookX = TasInputFrame.FromDeg(look.x * lookSensitivity);
            f.lookY = TasInputFrame.FromDeg(-look.y * lookSensitivity);

            if (ButtonReader != null) f.buttons = ButtonReader();
            else ReadDefaultButtons(ref f);

            // cursor position is only meaningful when the cursor is NOT locked; with
            // CursorLockMode.Locked it stops updating, which would freeze the aim pointer field.
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (!locked && Input.GetMouseButton(2))
            {
                f.aimX = TasInputFrame.FromNorm(Input.mousePosition.x / Mathf.Max(1f, Screen.width));
                f.aimY = TasInputFrame.FromNorm(Input.mousePosition.y / Mathf.Max(1f, Screen.height));
            }
            else
            {
                f.aimX = TasInputFrame.PointerAbsent;
                f.aimY = TasInputFrame.PointerAbsent;
            }

            f.pointerCount = 0;
            f.weaponSlot = (byte)Mathf.Clamp(SelectedSlot(), 0, 9);
            return f;
        }

        static void ReadDefaultButtons(ref TasInputFrame f)
        {
            f.Set(TasButton.Jump, Input.GetKey(KeyCode.Space));
            f.Set(TasButton.Fire, Input.GetMouseButton(0));
            f.Set(TasButton.AltFire, Input.GetMouseButton(1));
            f.Set(TasButton.Reload, Input.GetKey(KeyCode.R));
            f.Set(TasButton.Use, Input.GetKey(KeyCode.E));
            f.Set(TasButton.Crouch, Input.GetKey(KeyCode.C) || Input.GetKey(KeyCode.LeftControl));
            f.Set(TasButton.Walk, Input.GetKey(KeyCode.LeftShift));
            f.Set(TasButton.Sprint, Input.GetKey(KeyCode.LeftShift) && f.moveY > 0);
            f.Set(TasButton.AimDown, Input.GetKey(KeyCode.Mouse1));
            f.Set(TasButton.Buy, Input.GetKey(KeyCode.B));
            if (Input.GetKeyDown(KeyCode.Alpha1)) f.Set(TasButton.Slot1, true);
            if (Input.GetKeyDown(KeyCode.Alpha2)) f.Set(TasButton.Slot2, true);
            if (Input.GetKeyDown(KeyCode.Alpha3)) f.Set(TasButton.Slot3, true);
            if (Input.GetKeyDown(KeyCode.Alpha4)) f.Set(TasButton.Slot4, true);
            if (Input.GetKeyDown(KeyCode.Alpha5)) f.Set(TasButton.Slot5, true);
        }

        /// <summary>Slot keys are edge-ish, so a sticky held state is wrong: resolve from your game.</summary>
        static int SelectedSlot()
        {
            return 0;   // ⚠ return your current weapon index (it is recorded for checksums, not for driving)
        }

        static Vector2 MouseDelta()
        {
            Vector3 now = Input.mousePosition;
            Vector2 d = haveLast ? new Vector2(now.x - lastMouse.x, now.y - lastMouse.y) : Vector2.zero;
            lastMouse = now;
            haveLast = true;

            // With the cursor locked, mousePosition is frozen; the axis is the only delta source.
            if (Cursor.lockState == CursorLockMode.Locked)
                d = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) / Mathf.Max(0.0001f, mouseDegreesPerPixel);
            return d;
        }

        /// <summary>Call when the game (un)pauses or the cursor mode changes, so no giant delta is recorded.</summary>
        public static void ResyncCursor()
        {
            lastMouse = Input.mousePosition;
            haveLast = true;
            lastLock = Cursor.lockState;
        }
    }
}
