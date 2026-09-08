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
            // pin it while RECORDING too, or the run's own definition of "can I turn" is unstable
            TasTool tool = TasTool.i;
            if (tool != null) tool.OnRecordStart += delegate { LockCursorForReplay(true); };
            TasInput.TapeEngaged += delegate { TasUnityBridge.ResetTransient(); PinScreenInch(LastTapeInch()); };
            TasInput.TapeReleased += delegate
            {
                TasInputBus.Disengage();
                if (TasTool.i == null || TasTool.i.session != TasSession.Recording) LockCursorForReplay(false);
                RestoreScreenInch();
                TasUnityBridge.ResetTransient();
            };

            // anything reading InputData directly (not through GetInputData) must be served too
            if (gm != null && gm.m_playerController != null) gm.m_playerController.inputData = im.input;

            // No LiveSampler wiring on purpose: playback's off-tape check needs a PURE device read, and
            // this bridge's sampler is the capture path (during replay it returns the tape-fed struct, so
            // the comparison would be vacuously true). TasPlayback falls back to TasLiveInput
            // PeekButtons(), which reads only the buttons and consumes nothing.
            TasPlayback.CursorUnlockedProbe = CursorUnlocked;
            TasRecorder.TapeValidator = ValidateTape;
            TasRecorder.HeaderProbe = FillHeader;
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
            // Tick-exact cursor policy for the recording path: pinning here happens on the same tick the
            // CursorUnlocked flag below is stamped, so the pin and the tape can never disagree by a
            // frame. Replay is pinned by TasGameBridge.Update (its sampler is device-only now) and
            // released there too, since that is the only place that sees a session END by any path.
            if (pinCursor && !locking) LockCursorForReplay(true);

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
                // ControlTypesManager.DisableAll() does SetActive(false) on the whole
                // SplitTouchControl GameObject, and nothing zeroes dt3/jump on the way out. So for
                // Standart/Autowalk that object is INACTIVE with whatever the last touch left in it,
                // and GetInput() reads that stale value every tick. Fold it in only while it is live;
                // the stale case is recorded through the controller's resolved aux channels anyway,
                // which is what makes a replay of a run made in that state reproduce it exactly.
                SplitTouchControl st = SplitTouchControl.i;
                if (st != null && st.isActiveAndEnabled)
                {
                    if (st.jump) f.Set(TasButton.Jump, true);
                    f.moveX = TasInputFrame.FromFloat(Mathf.Clamp(st.dt3.x * 10f, -1f, 1f));
                    f.moveY = TasInputFrame.FromFloat(Mathf.Clamp(st.dt3.y * 5f, -1f, 1f));
                }

                if (CursorUnlocked) f.flags |= (byte)TasFrameFlags.CursorUnlocked;
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

        // ------------------------------------------------------------------ MouseLook: nothing to snapshot
        //
        // I warned you last round that MouseLook's private accumulators would reset your aim on
        // rollback. Your version does not have that problem, and the reason is in LookRotation():
        //
        //     float yRot = mouse_x * XSensitivity;
        //     character.localRotation = character.localRotation * Quaternion.Euler(0, yRot, 0);
        //
        // It reads the CURRENT localRotation and multiplies, instead of accumulating into
        // m_CharacterTargetRot like the stock asset. So the rotation lives in the transforms, which
        // the ring snapshot already restores, and mouse_x/mouse_y are per-frame inputs that the
        // controller overwrites before every use. No snapshot needed, no reflection, no link.xml.
        // m_CharacterTargetRot / m_CameraTargetRot are written by Init/SetLook and read by nobody:
        // dead state. And smooth/smoothTime are never consulted, so there is no smoothing to desync.
        //
        // What DOES matter in this file is documented in Docs/TasControllerAudit.md §3: the clamp
        // divides by q.w, so if MinimumX/MaximumX are ever widened past +-90 the camera can produce
        // NaN, and XSensitivity/YSensitivity/clampVerticalRotation/MinimumX/MaximumX are physics.
        static void SaveLook(object ml, BinaryWriter w) { w.Write(0); }        // intentionally empty
        static void LoadLook(object ml, BinaryReader r) { if (r.ReadInt32() != 0) r.ReadSingle(); r.ReadSingle(); }

        /// <summary>
        /// `RotateView()` early-returns whenever the cursor is unlocked, but `Update()` still does
        /// `lookAccum += Mathf.Abs(inputData.look_x)` - so an unlocked cursor means "no turning, but
        /// the look-derived bunny gain keeps accruing". That is an exploit surface AND a tape poison:
        /// a run recorded with the cursor unlocked replays differently once it is locked. Pin it for
        /// the whole session (record and replay) and stamp the flag per tick so a mismatch is
        /// explainable rather than mysterious. Escape unlocks it via InputManager.KeyboardSupport.
        /// </summary>
        public static bool pinCursor = true;

        public static bool CursorUnlocked { get { return Cursor.lockState != CursorLockMode.Locked; } }

        /// <summary>True while the session owns the cursor. Read by TasGameBridge's release path.</summary>
        public static bool IsCursorPinned { get { return locking; } }

        public static void LockCursorForReplay(bool on)
        {
            if (!pinCursor) return;
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
        /// Control type and sensitivity are read once per run (Reload) and once per tick (gain)
        /// respectively, so a tape is only meaningful against them. Refuse with a real message rather
        /// than letting configHash say "X8 != Y8".
        /// </summary>
        public static string ValidateTape(TasTape t)
        {
            if (t == null) return "no tape";

            // ControlTypesManager.Reload() reads Prefs.ControlType ONCE per run, but GetInput() reads
            // GameState.ControlTypeCache EVERY tick and Autowalk/Tap force yy = 1f. So a tape recorded
            // in Tap mode replayed under Standart is a different game, not a different player.
            if (t.header.controlType >= 0 && (int)Prefs.ControlType != t.header.controlType)
                return "control type mismatch: tape was " + (ControlTypes)t.header.controlType +
                       ", this build is " + Prefs.ControlType +
                       " (Reload() only reads it at run start, so switch it and start a fresh run)";

            // MouseLook's clamp divides by q.w: if MinimumX/MaximumX were widened past +-90 on this
            // prefab, pitch can hit NaN and every tape recorded before that edit is void.
            RigidbodyFirstPersonController cc = C;
            if (cc != null && cc.mouseLook != null &&
                (cc.mouseLook.MinimumX < -90f || cc.mouseLook.MaximumX > 90f))
                return "MouseLook clamp is outside +-90 (" + cc.mouseLook.MinimumX + ".." + cc.mouseLook.MaximumX +
                       "), ClampRotationAroundXAxis divides by q.w -> NaN risk; tapes are not trustworthy";

            InputManager im = IM;
            if (im != null && t.header.screenInch > 0f && !Mathf.Approximately(t.header.screenInch, im.screenInch))
                TasUnityBridge.PinScreenInch(t.header.screenInch);   // replay-safe: the header wins
            // dbg_sens is written every RotateView, so the LAST recorded tick carries the sensitivity
            // the run actually used - better than trusting a header number if it changed mid-run.
            if (cc != null && cc.dbg_sens > 0f && !Mathf.Approximately(cc.dbg_sens, Prefs.Sensivity))
                Debug.Log("[TAS] note: the controller's last observed sensitivity was " + cc.dbg_sens.ToString("0.###") +
                          ", this build's Prefs.Sensivity is " + Prefs.Sensivity.ToString("0.###") +
                          ". configHash is what refuses a mismatched tape; this is just so you can see it.");
            return null;
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


        /// <summary>
        /// Everything in this controller's maths that is not in the tape. A run recorded with
        /// sensitivity 2.4 must not silently replay at 1.0, and `if (!Application.isMobilePlatform)
        /// sensMultiplier = 1f` means the PLATFORM is part of the physics, as is the
        /// `if (sens &lt; 1f)` branch that scales the strafe/look gain but not the turn.
        /// </summary>
        /// <summary>
        /// The identity half of a tape. Same fields your DemoRecorder.ReloadPlayerData fills, because a
        /// tape that cannot say map + nick + rank + cosmetics cannot be matched to a leaderboard entry -
        /// and because mapname is what mapHash is taken over, an empty mapname would hash every map alike.
        /// Called at recording start AND at save, so a late-joining player name still lands.
        /// </summary>
        static void FillHeader(TasTapeHeader h)
        {
            // ⚠ MEMBER NAMES BELOW are from your DemoRecorder.ReloadPlayerData, which I have not read -
            // they are the one part of this file that can fail to compile for naming reasons rather than
            // design reasons. If a line is red, it is a name fix, not a structural problem: the tape is
            // valid without them, it just loses its identity.
            h.mapname = MapIsimHelpers.ServerMapIsimGetir();
            h.nick = Prefs.ServerNick;
            h.flagId = Prefs.SelectedFlag;
            h.avatarId = Prefs.SelectedAvatar;
            h.knifeId = InventoryHelpers.aktifBicak;
            h.capeId = InventoryHelpers.aktifCape;
            h.gloveId = InventoryHelpers.aktifEldiven;
            h.effectId = InventoryHelpers.aktifPlayerEffect;
            h.rankStr = GameManagerHelpers.RankBelirle();
            h.rankIdx = GameManagerHelpers.RankIndexBul(h.rankStr);
            h.controlType = (int)Prefs.ControlType;
            h.controlTypeStr = Prefs.ControlType.ToString();
            GameManager gm = TasGameBridge.i != null ? TasGameBridge.i.Manager : null;
            if (gm != null && gm.m_TimeManager != null) h.gameRunSeconds = gm.m_TimeManager.Seconds;
        }

        public static uint ConfigExtras()
        {
            uint h = 2166136261u;
            h = TasRng.Mix(h, Prefs.Sensivity);
            h = TasRng.Mix(h, Mathf.RoundToInt(CurrentScreenInch * 10000f));
            // Both, deliberately: GameState.ControlTypeCache is what the physics reads THIS tick, while
            // Prefs.ControlType is what the NEXT ControlTypesManager.Reload() will install. They differ
            // exactly when a control-type change is pending, and that change is a run-semantics change.
            h = TasRng.Mix(h, (int)GameState.ControlTypeCache);
            h = TasRng.Mix(h, (int)Prefs.ControlType);
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

                // MouseLook: these five are consulted every tick by LookRotation/ClampRotation.
                // (smooth/smoothTime and the two target quaternions are not, so they are not hashed.)
                MouseLook ml = c.mouseLook;
                if (ml != null)
                {
                    h = TasRng.Mix(h, ml.XSensitivity);
                    h = TasRng.Mix(h, ml.YSensitivity);
                    h = TasRng.Mix(h, ml.MinimumX);
                    h = TasRng.Mix(h, ml.MaximumX);
                    h = TasRng.Mix(h, ml.clampVerticalRotation ? 16 : 0);
                }
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
