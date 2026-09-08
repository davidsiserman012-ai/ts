using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Tas;
using UnityStandardAssets.Characters.FirstPerson;
using UnityEngine;

/// <summary>
/// The whole contract between your game and the tape. Your InputManager already calls
/// TasInputBus.Active / TasInputBus.Current / TasUnityBridge.BuildInputData, and
/// Integration/TasGameBridge.cs calls Bind() once at boot.
///
/// Two directions, and after reading the controller there are TWO INJECTION POINTS, not one:
///
///   record  InputData + the controller's own resolved fields  --FromController-->  tape
///   replay  tape --BuildInputData--> InputData   AND   tape --TryResolvedInput--> GetInput()
///
/// The second one is not optional for Tap/Analog control types: GetInput() folds in
/// SplitTouchControl.dt3, ButonManager.GetTapX() and uinput_x/uinput_y, none of which are InputData,
/// so a tape of InputData alone cannot reproduce those runs. It also gives you a free integrity check
/// on keyboard, where the two paths must agree.
/// </summary>
namespace UnityTAS
{
    public static class TasInputBus
    {
        public static bool Active { get; internal set; }
        public static TasInputFrame Current { get; internal set; }
        public static TasInputFrame LastLive;
        public static long Tick;

        public static event Action<TasInputFrame> OnFeed;
        public static event Action OnRelease;

        internal static void Engage(TasInputFrame f)
        {
            Current = f;
            Tick = TasClock.Tick;
            if (!Active) Active = true;
            if (OnFeed != null) OnFeed(f);
        }

        internal static void Disengage()
        {
            if (!Active) return;
            Active = false;
            Current = default(TasInputFrame);
            if (OnRelease != null) OnRelease();
        }
    }

    public static class TasUnityBridge
    {
                static RigidbodyFirstPersonController C
        {
            get
            {
                if (TasGameBridge.i != null && TasGameBridge.i.Controller != null) return TasGameBridge.i.Controller;
                return GameState.playerRigidBody as RigidbodyFirstPersonController;
            }
        }
        static InputManager IM { get { return InputManager.i; } }

        /// <summary>
        /// true = during replay, GetInput() returns the tape's resolved pair instead of
        /// re-deriving it. Needed for Tap/Analog; harmless-but-redundant on keyboard.
        /// </summary>
        public static bool OverrideResolvedInput = true;

        /// <summary>
        /// RotateView() begins with `if (Cursor.lockState != Locked) return;`, so an unlocked cursor
        /// means the replayed player never turns - and the failure is silent and total. Force it for
        /// the duration of playback, and hand it back after. (Manual mode additionally bails on
        /// `Mathf.Abs(Time.timeScale) < float.Epsilon`, which is why the tool must never freeze the
        /// world with timeScale: see Docs/TasControllerAudit.md.)
        /// </summary>
        static CursorLockMode prevLock = CursorLockMode.Locked;
        static bool prevVisible;
        static bool locking;

        public static void Bind(GameManager gm)
        {
            InputManager im = InputManager.i;
            if (im == null) return;

            TasLiveInput.SampleOverride = SampleFromController;
            TasInput.TapeApplier = delegate (TasInputFrame f)
            {
                TasInputBus.Engage(f);
                BuildInputData(f);                      // write immediately: InputManager may not tick this frame
                LockCursorForReplay(true);
            };
            TasInput.TapeEngaged += delegate { TasUnityBridge.ResetTransient(); PinScreenInch(LastTapeInch()); };
            TasInput.TapeReleased += delegate
            {
                TasInputBus.Disengage();
                LockCursorForReplay(false);
                RestoreScreenInch();
                TasUnityBridge.ResetTransient();
            };

            // anything reading InputData directly (not through GetInputData) must be served too
            if (gm != null && gm.m_playerController != null) gm.m_playerController.inputData = im.input;

            TasPlayback.LiveSampler = SampleFromController;
            Debug.Log("[TAS] bridge bound | InputData is a struct (safe copies), screenInch=" +
                      im.screenInch.ToString("0.000") + ", resolvedOverride=" + OverrideResolvedInput);
        }

        // ------------------------------------------------------------------ record side

        /// <summary>
        /// Called by the recorder at the clock's CAPTURE point (LateUpdate) - i.e. after
        /// InputManager.Update wrote the frame's input and after the controller consumed it, which is
        /// the only instant where "what the sim ate" is well defined in this codebase.
        /// </summary>
        public static TasInputFrame SampleFromController()
        {
            InputManager im = IM;
            if (im == null) return default(TasInputFrame);

            TasInputFrame f = TasUnityBridge.FromInputData(im.input);
            TasInputBus.LastLive = f;

            RigidbodyFirstPersonController c = C;
            if (c != null)
            {
                // the resolved pair + the strafe accumulator, straight off the component that used them
                f.ResolvedXSet(c.input_x, c.yy);
                f.ResolvedStrafe(c.inputDataX, c.xvel);

                // `held = inputData.jump || Input.GetButton("Jump")` in Update() means a live
                // spacebar is an input path the tape cannot see. Fold the DEVICE half into the Jump bit
                // so it is recorded; the replay then reproduces it as part of the frame (and the
                // recommended one-line patch removes the leak entirely - see the audit doc).
                if (Input.GetKey(KeyCode.Space)) f.Set(TasButton.Jump, true);

                // Only fields this controller is proven to read (jump, dt3) - no guessing at the rest
                // of your touch layer, because a guessed field either fails to compile or, worse,
                // records a value the sim never used.
                SplitTouchControl st = SplitTouchControl.i;
                if (st != null)
                {
                    if (st.jump) f.Set(TasButton.Jump, true);
                    f.moveX = TasInputFrame.FromFloat(Mathf.Clamp(st.dt3.x * 10f, -1f, 1f));
                    f.moveY = TasInputFrame.FromFloat(Mathf.Clamp(st.dt3.y * 5f, -1f, 1f));
                }
            }
            return f;
        }

        public static InputData FromInputDataOnly(InputData d) { return FromInputData(d); }

        public static TasInputFrame FromInputData(InputData d)
        {
            TasInputFrame f = new TasInputFrame();
            f.Set(TasButton.DirLeft, d.left);
            f.Set(TasButton.DirRight, d.right);
            f.Set(TasButton.DirFwd, d.fwd);
            f.Set(TasButton.DirBck, d.bck);
            f.Set(TasButton.DirUp, d.up);
            f.Set(TasButton.DirDown, d.down);
            f.Set(TasButton.Jump, d.jump);
            f.Set(TasButton.Sprint, d.run);
            f.moveX = TasInputFrame.FromFloat(Mathf.Clamp(d.analog_x, -1f, 1f));
            f.moveY = TasInputFrame.FromFloat(Mathf.Clamp(d.analog_y, -1f, 1f));
            // look_x/look_y are already post-GetAxis-filter and already divided by 4; store exactly
            // what the controller saw, x1000, so nothing is re-filtered or re-scaled on replay.
            f.lookX = TasInputFrame.FromDeg(d.look_x);
            f.lookY = TasInputFrame.FromDeg(d.look_y);
            f.pointerCount = (byte)Mathf.Min(255, Input.touchCount);
            f.weaponSlot = 0;
            return f;
        }

        // ------------------------------------------------------------------ replay side

        /// <summary>Tape -> InputData. See the class comment for why direction and inputLastFrame matter.</summary>
        public static InputData BuildInputData(TasInputFrame f)
        {
            InputManager im = IM;
            InputData d = im != null ? im.input : default(InputData);

            d.left = f.Has(TasButton.DirLeft);
            d.right = f.Has(TasButton.DirRight);
            d.fwd = f.Has(TasButton.DirFwd);
            d.bck = f.Has(TasButton.DirBck);
            d.up = f.Has(TasButton.DirUp);
            d.down = f.Has(TasButton.DirDown);
            d.jump = f.Has(TasButton.Jump);
            d.run = f.Has(TasButton.Sprint);
            d.analog_x = f.MoveXf;
            d.analog_y = f.MoveYf;
            d.look_x = f.LookXf;
            d.look_y = f.LookYf;

            // CalculateDirection() is not re-run for a short-circuited GetInputData(), and this
            // controller's GetInput()/inputDataX read the bools, not direction - but LevelFizik and
            // anything else that reads inputData.direction must not see the player's real keys.
            // Same six-term order as CalculateDirection, because Vector3 addition is not associative.
            Vector3 dir = Vector3.zero;
            if (d.left) dir += Vector3.left;
            if (d.right) dir += Vector3.right;
            if (d.up) dir += Vector3.up;
            if (d.down) dir += Vector3.down;
            if (d.fwd) dir += Vector3.forward;
            if (d.bck) dir += Vector3.back;
            d.direction = dir;

            // lastBunnyFrame gating: `if (lastBunnyFrame != inputData.inputLastFrame) push;`
            // Stamping the live frame count keeps the same "changed since the last physics step"
            // semantics the recorder saw. It only ever compares for INEQUALITY, so any per-tick
            // monotonic value works - but it must change exactly once per tick, which is another
            // reason enforceFixedRate is not optional for this game.
            d.inputLastFrame = Time.frameCount;

            if (im != null) im.input = d;      // struct: a copy write. Nothing aliases it.
            return d;
        }

        /// <summary>
        /// The one line to add at the end of RigidbodyFirstPersonController.GetInput():
        /// 
        ///     if (UnityTAS.TasUnityBridge.TryResolvedInput(out resolved)) return resolved;
        ///     return new Vector2(input_x, yy);
        /// 
        /// That is the seam for Tap/Analog runs, where input_x/input_y come from ButonManager and
        /// SplitTouchControl rather than from InputData.
        /// </summary>
        public static bool TryResolvedInput(out Vector2 resolved)
        {
            resolved = Vector2.zero;
            if (!OverrideResolvedInput || !TasInputBus.Active) return false;
            TasInputFrame f = TasInputBus.Current;
            resolved = new Vector2(f.ResolvedX, f.ResolvedY);
            return true;
        }

        /// <summary>
        /// Not needed as an input patch: inputDataX and xvel are both derived from inputData.left/right,
        /// which the DirLeft/DirRight bits already carry. Kept as read-only integrity signals, because a
        /// mismatch there would mean something else is writing them.
        /// </summary>
        public static bool TryResolvedStrafe(out float inputDataX, out float xvel)
        {
            inputDataX = 0f; xvel = 0f;
            if (!OverrideResolvedInput || !TasInputBus.Active) return false;
            TasInputFrame f = TasInputBus.Current;
            inputDataX = f.InputDataX;
            xvel = f.Xvel;
            return true;
        }

        // ------------------------------------------------------------------ savestates for this controller

        /// <summary>
        /// MouseLook is a [Serializable] CLASS field on the controller, so nothing in the transform or
        /// rigidbody snapshot covers its internal yaw/pitch accumulators. Restore the transforms without
        /// restoring those and the next RotateView recomputes rotation from stale state - which is the
        /// "savestate resets my aim" bug every Unity rollback tool gets. Fields are found by name
        /// because your MouseLook is modified (it has public mouse_x/mouse_y), and the two rotation
        /// accumulators are read/written by reflection so this file still works if they are private.
        /// ⚠ send MouseLook.cs and this becomes 4 direct field reads.
        /// </summary>
        static readonly string[] LookFieldCandidates =
        {
            "m_XRotation", "m_YRotation", "m_xRotation", "m_yRotation",
            "xRotation", "yRotation", "m_RotationX", "m_RotationY"
        };

        static FieldInfo[] lookFields;
        static bool lookFieldsSearched;
        static readonly Dictionary<object, float[]> lookPrev = new Dictionary<object, float[]>(ReferenceComparer<object>.Instance);

        static void EnsureLookFields(object ml)
        {
            if (lookFieldsSearched) return;
            lookFieldsSearched = true;
            if (ml == null) return;
            var list = new List<FieldInfo>(2);
            Type t = ml.GetType();
            while (t != null && list.Count < 2)
            {
                foreach (string name in LookFieldCandidates)
                {
                    if (list.Count >= 2) break;
                    FieldInfo fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                                                          BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (fi != null && fi.FieldType == typeof(float) && !list.Contains(fi)) list.Add(fi);
                }
                t = t.BaseType;
            }
            lookFields = list.Count == 2 ? list.ToArray() : null;
            if (lookFields == null)
                Debug.LogWarning("[TAS] MouseLook rotation fields not found on " + ml.GetType().Name +
                                 " - rollback will reset aim. Send MouseLook.cs, or add " +
                                 "public float TasX, TasY mirrored in LookRotation().");
        }

        public static void SaveLook(object ml, BinaryWriter w)
        {
            if (ml == null) { w.Write(0); return; }
            EnsureLookFields(ml);
            if (lookFields == null) { w.Write(0); return; }
            w.Write(1);
            w.Write((float)lookFields[0].GetValue(ml));
            w.Write((float)lookFields[1].GetValue(ml));
        }

        public static void LoadLook(object ml, BinaryReader r)
        {
            if (r.ReadInt32() == 0) { if (ml != null) { /* no state known: re-sync from the restored body */ } return; }
            float a = r.ReadSingle(), b = r.ReadSingle();
            if (ml == null || lookFields == null) return;
            lookFields[0].SetValue(ml, a);
            lookFields[1].SetValue(ml, b);
        }

        /// <summary>
        /// Everything on the controller that is per-run state and NOT position/velocity. Derived by
        /// reading which fields this class writes to but never resets - that set is the run.
        /// </summary>
        public static void SaveController(RigidbodyFirstPersonController c, BinaryWriter w)
        {
            if (c == null) { w.Write(0); return; }
            w.Write(1);
            w.Write(c.TasYaw);
            w.Write(c.m_PreviouslyGrounded);
            w.Write(c.m_IsGrounded);
            w.Write(c.m_Jumping);
            w.Write(c.m_Jump);
            w.Write(c.active180);
            w.Write(c.TasLookAccum);
            w.Write(c.lastBunnyFrame);
            w.Write(c.xvel);
            w.Write(c.inputDataX);
            w.Write(c.input_x);
            w.Write(c.input_y);
            w.Write(c.uinput_x);
            w.Write(c.uinput_y);
            w.Write(c.tap_x);
            w.Write(c.dynamic_x);
            w.Write(c.yy);
            w.Write(c.mag);
            w.Write(c.mag2);
            w.Write(c.ag);
            w.Write(c.ag2);
            w.Write(c.ag3);
            w.Write(c.speed);
            w.Write(c._lastjump);
            w.Write(c._bmax);
            w.Write(c.m_bunnyCarpan);
            w.Write(c.m_bunnyYokCarpan);
            w.Write(c.m_bunnyCarpanDelta);
            w.Write(c.sensMultiplier);
            w.Write(c.total_speed);
            w.Write(c.total_carpan);
            w.Write(c.total_frame);
            w.Write(c.total_frame_carpan);
            w.Write(c.total_force);
            w.Write(c.max_speed);
            w.Write(c.max_carpan);
            w.Write(c.max_carpan1);
            w.Write(c.max_carpan2);
            w.Write(c.max_yfactor);
            // velXZ / vel / velLocal are re-read by RotateView BEFORE the next step, so they are
            // restored explicitly rather than relying on the rigidbody capture order.
            w.Write(c.velXZ);
            w.Write(c.vel);
            w.Write(c.velLocal);
            w.Write(c.inputData.direction);
            w.Write(c.inputData.inputLastFrame);
            SaveLook(c.mouseLook, w);
            w.Write(LevelFizik.MaxHizYDisabled);        // a STATIC that gates the velocity clamp
            w.Write(GameState.SurfModeFizik);
            w.Write(GameState.TapMode);
            w.Write(GameState.SurfSliding);
            w.Write((int)GameState.ControlTypeCache);
        }

        public static void LoadController(RigidbodyFirstPersonController c, BinaryReader r)
        {
            if (r.ReadInt32() == 0) return;
            if (c == null) { Debug.LogWarning("[TAS] controller gone; skipping controller state"); return; }
            c.TasYaw = r.ReadSingle();
            c.m_PreviouslyGrounded = r.ReadBoolean();
            c.m_IsGrounded = r.ReadBoolean();
            c.m_Jumping = r.ReadBoolean();
            c.m_Jump = r.ReadBoolean();
            c.active180 = r.ReadBoolean();
            c.TasLookAccum = r.ReadSingle();
            c.lastBunnyFrame = r.ReadInt32();
            c.xvel = r.ReadSingle();
            c.inputDataX = r.ReadSingle();
            c.input_x = r.ReadSingle();
            c.input_y = r.ReadSingle();
            c.uinput_x = r.ReadSingle();
            c.uinput_y = r.ReadSingle();
            c.tap_x = r.ReadSingle();
            c.dynamic_x = r.ReadSingle();
            c.yy = r.ReadSingle();
            c.mag = r.ReadSingle();
            c.mag2 = r.ReadSingle();
            c.ag = r.ReadSingle();
            c.ag2 = r.ReadSingle();
            c.ag3 = r.ReadSingle();
            c.speed = r.ReadSingle();
            c._lastjump = r.ReadSingle();
            c._bmax = r.ReadSingle();
            c.m_bunnyCarpan = r.ReadSingle();
            c.m_bunnyYokCarpan = r.ReadSingle();
            c.m_bunnyCarpanDelta = r.ReadSingle();
            c.sensMultiplier = r.ReadSingle();
            c.total_speed = r.ReadSingle();
            c.total_carpan = r.ReadSingle();
            c.total_frame = r.ReadSingle();
            c.total_frame_carpan = r.ReadSingle();
            c.total_force = r.ReadSingle();
            c.max_speed = r.ReadSingle();
            c.max_carpan = r.ReadSingle();
            c.max_carpan1 = r.ReadSingle();
            c.max_carpan2 = r.ReadSingle();
            c.max_yfactor = r.ReadSingle();
            c.max_speed = r.ReadSingle();
            c.velXZ = r.ReadVector3();
            c.vel = r.ReadVector3();
            c.velLocal = r.ReadVector3();
            InputData d = c.inputData;
            d.direction = r.ReadVector3();
            d.inputLastFrame = r.ReadInt32();
            c.inputData = d;
            LoadLook(c.mouseLook, r);
            LevelFizik.MaxHizYDisabled = r.ReadBoolean();
            GameState.SurfModeFizik = r.ReadBoolean();
            GameState.TapMode = r.ReadBoolean();
            GameState.SurfSliding = r.ReadBoolean();
            GameState.ControlTypeCache = (ControlTypes)r.ReadInt32();
        }

        // ------------------------------------------------------------------ config / environment

        public static float CurrentScreenInch { get { return IM != null ? IM.screenInch : 0f; } }

        static float platformScreenInch = -1f;
        static float lastTapeInch;

        static float LastTapeInch()
        {
            TasTape t = TasTool.i != null ? TasTool.i.LastTape : null;
            return t != null ? t.header.screenInch : 0f;
        }

        public static void PinScreenInch(float value)
        {
            InputManager im = IM;
            if (im == null) return;
            if (platformScreenInch < 0f) platformScreenInch = im.screenInch;
            lastTapeInch = value;
            im.screenInch = value > 0f ? value : (platformScreenInch > 0f ? platformScreenInch : 1f);
        }

        public static void RestoreScreenInch()
        {
            if (IM != null && platformScreenInch > 0f) IM.screenInch = platformScreenInch;
        }

        public static void ResetTransient()
        {
            InputManager im = IM;
            if (im != null) { im.ResetVars(); im.ResetInputData(); }
            TasLiveInput.ResyncCursor();
        }

        static void LockCursorForReplay(bool on)
        {
            if (on && !locking)
            {
                locking = true;
                prevLock = Cursor.lockState;
                prevVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (!on && locking)
            {
                locking = false;
                Cursor.lockState = prevLock;
                Cursor.visible = prevVisible;
            }
        }

        /// <summary>
        /// Everything in this controller's maths that is not in the tape. A run recorded with
        /// sensitivity 2.4 must not silently replay at 1.0, and `if (!Application.isMobilePlatform)
        /// sensMultiplier = 1f` means the PLATFORM is part of the physics, as is the
        /// `if (sens &lt; 1f)` branch that scales the strafe/look gain but not the turn.
        /// </summary>
        public static uint ConfigExtras()
        {
            uint h = 2166136261u;
            h = TasRng.Mix(h, Prefs.Sensivity);
            h = TasRng.Mix(h, Mathf.RoundToInt(CurrentScreenInch * 10000f));
            h = TasRng.Mix(h, (int)GameState.ControlTypeCache);
            h = TasRng.Mix(h, Application.isMobilePlatform ? 1 : 2);
            h = TasRng.Mix(h, (int)Application.platform);
            h = TasRng.Mix(h, LevelFizik.MaxHizYDisabled ? 4 : 0);
            h = TasRng.Mix(h, GameState.SurfModeFizik ? 8 : 0);
            RigidbodyFirstPersonController c = C;
            if (c != null)
            {
                h = TasRng.Mix(h, c.BunnyArtisHizi);
                h = TasRng.Mix(h, c.BunnyMaxHiz);
                h = TasRng.Mix(h, c.BunnyMaxHizTap);
                h = TasRng.Mix(h, c.YerdeMaxHiz);
                h = TasRng.Mix(h, c.YerdeHizlanma);
                h = TasRng.Mix(h, c.YerdeYavaslama);
                h = TasRng.Mix(h, c.HavadaGazKesme);
                h = TasRng.Mix(h, c.BunnysizYavaslamaHizi);
                h = TasRng.Mix(h, c.SurfMaxHiz);
                h = TasRng.Mix(h, c.SurfMaxHizDikey);
                h = TasRng.Mix(h, c.Don180MaxHiz);
                h = TasRng.Mix(h, c.DonmeHizi);
                h = TasRng.Mix(h, c.DonmeHizKatlayici);
                h = TasRng.Mix(h, c.MaxHizY);
                h = TasRng.Mix(h, c.movementSettings.JumpForce);
                h = TasRng.Mix(h, c.SurfSlideMaxSpeed);
                h = TasRng.Mix(h, c.advancedSettings.groundCheckDistance);
                h = TasRng.Mix(h, c.advancedSettings.stickToGroundHelperDistance);
                h = TasRng.Mix(h, c.advancedSettings.shellOffset);
                h = TasRng.Mix(h, c.advancedSettings.airControl ? 1 : 0);
                h = TasRng.Mix(h, RigidbodyFirstPersonController.analog_bunny_mult);
            }
            return h;
        }
    }

    /// <summary>Reference identity, so a per-instance MouseLook cache does not depend on GetHashCode.</summary>
    sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
        public bool Equals(T a, T b) { return ReferenceEquals(a, b); }
        public int GetHashCode(T o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
    }
}
