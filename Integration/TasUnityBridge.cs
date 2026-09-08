using System;
using Tas;
using UnityEngine;

/// <summary>
/// The namespace your InputManager already imports. This file is the whole contract between
/// `InputManager.GetInputData()` and the tape:
///
///     if (TasInputBus.Active)
///         return TasUnityBridge.BuildInputData(TasInputBus.Current);
///     return controlEnabled ? input : emptyInput;
///
/// That short-circuit is the right shape, and it's better than what I had you do last round
/// (disabling InputManager during replay): the live path can keep running and accumulating touch
/// deltas and cursor state, so when replay ends the player is not holding a stale stick. Keep it.
/// </summary>
namespace UnityTAS
{
    public static class TasInputBus
    {
        /// <summary>True for exactly as long as the tape is feeding input. Read by GetInputData().</summary>
        public static bool Active { get; internal set; }

        /// <summary>The frame the sim must consume for the current tick.</summary>
        public static TasInputFrame Current { get; internal set; }

        /// <summary>Last state read off the live path, for the overlay and for the recorder.</summary>
        public static TasInputFrame LastLive;

        /// <summary>Tick index of the frame currently being fed / captured, for logging.</summary>
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

        /// <summary>Record path: called by the adapter at the capture point, from the live struct.</summary>
        public static TasInputFrame SampleLive()
        {
            InputManager im = InputManager.i;
            if (im == null) return default(TasInputFrame);
            TasInputFrame f = TasUnityBridge.FromInputData(im.GetInputData());
            LastLive = f;
            return f;
        }
    }

    public static class TasUnityBridge
    {
        /// <summary>
        /// Tape -> game. Sets every field the sim can possibly read, including the two that are easy
        /// to miss:
        ///
        ///  * input.direction - CalculateDirection() computes it in InputManager.Update from the six
        ///    bools. During replay we short-circuit GetInputData, so the live calculation describes the
        ///    PLAYER's keys, not the tape. Recompute it here from the fed bits or the replayed run
        ///    moves in the direction the last key press implies.
        ///  * input.inputLastFrame - if anything treats `!= Time.frameCount` as "stale input", a
        ///    frozen value makes the sim ignore the tape entirely. Stamp it every call.
        ///
        /// Mutating InputManager.i.input (rather than returning a detached struct) keeps working
        /// whether InputData is a struct or a class - which is the one thing I still cannot tell from
        /// the file, and it matters: GameManager.FirstInputRoutine captures `inputData` ONCE before
        /// its loop, so if InputData is a struct then lookList[] is the same value ten times.
        /// </summary>
        public static InputData BuildInputData(TasInputFrame f)
        {
            InputManager im = InputManager.i;
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

            Vector3 dir = Vector3.zero;
            if (d.left) dir += Vector3.left;
            if (d.right) dir += Vector3.right;
            if (d.up) dir += Vector3.up;
            if (d.down) dir += Vector3.down;
            if (d.fwd) dir += Vector3.forward;
            if (d.bck) dir += Vector3.back;
            d.direction = dir;

            d.inputLastFrame = Time.frameCount;

            if (im != null) im.input = d;      // no-op difference for a struct, correct for a class
            return d;
        }

        /// <summary>Game -> tape. Read the live struct after InputManager.Update wrote it.</summary>
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
            f.lookX = TasInputFrame.FromDeg(d.look_x);
            f.lookY = TasInputFrame.FromDeg(d.look_y);
            f.pointerCount = (byte)Mathf.Min(255, Input.touchCount);
            return f;
        }

        /// <summary>
        /// The scalar that makes your touch movement resolution- and DPI-dependent:
        ///     analogX = screenInch * (t_delta_l_sum.x / Screen.width) * 3f
        /// It is computed once from Screen.dpi at Start(), so the same swipe means a different speed on
        /// another display. Recorded in the tape header and re-pinned during replay, so a run captured on
        /// your dev machine verifies on another box instead of drifting 3% per strafe.
        /// ⚠ also worth deciding (product question, not a bug): do you WANT screenInch in the run's
        /// definition at all? For a keyboard run it only affects Tap/Joystick control types.
        /// </summary>
        public static float CurrentScreenInch
        {
            get { return InputManager.i != null ? InputManager.i.screenInch : 0f; }
        }

        static float platformScreenInch = -1f;

        public static void PinScreenInch(float value)
        {
            InputManager im = InputManager.i;
            if (im == null) return;
            if (platformScreenInch < 0f) platformScreenInch = im.screenInch;
            im.screenInch = value > 0f ? value : (platformScreenInch > 0f ? platformScreenInch : 1f);
        }

        /// <summary>
        /// Clear the per-frame accumulators so that when the tape lets go, the first live frame does
        /// not inherit t_delta_l_sum / mpos_delta from before the replay. Without this, the player
        /// gets one enormous look/move jump the instant replay stops, and if you record right after a
        /// rollback-from-playback, that jump lands in the tape.
        /// </summary>
        public static void ResetTransient()
        {
            InputManager im = InputManager.i;
            if (im == null) return;
            im.ResetVars();
            im.ResetInputData();
            TasLiveInput.ResyncCursor();
        }
    }
}
