using System;
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

        /// <summary>
        /// Optional: when the game has its own input struct (yours does - InputData via
        /// InputManager.GetInputData()), replay should write THAT instead of only filling
        /// TasInput.Current. Set it from the game-side adapter.
        /// </summary>
        public static System.Action<TasInputFrame> TapeApplier;

        /// <summary>
        /// Fired when the tape takes / gives back control. Your game-side adapter MUST use these to
        /// switch InputManager off and on: if InputManager keeps polling the device in its Update, it
        /// overwrites whatever the tape wrote (order of two scripts in the same Update batch) and the
        /// replay silently fights the mouse. That is the single most likely "replay does nothing" bug.
        /// </summary>
        public static event Action TapeEngaged;
        public static event Action TapeReleased;
        static bool engaged;
        public static bool BlockLiveInput;       // true => player cannot touch the sim (playback/spectate)

        /// <summary>Called by TasClock at OnTickHead, before any sim code reads input.</summary>
        public static void Commit()
        {
            if (SourcedFromTape)
            {
                Current = Pending;
                // push into the game's own input surface too, so code that bypasses TasInput
                // (your controller reading InputManager directly) is driven by the tape as well
                if (TapeApplier != null) TapeApplier(Current);
                return;
            }
            if (LiveReader == null) { Current = default(TasInputFrame); return; }
            if (BlockLiveInput) { Current = default(TasInputFrame); Previous = Current; return; }
            Current = LiveReader();
            Pending = Current;                   // keep the two views consistent for UI
        }

        public static void PushFromTape(TasInputFrame f)
        {
            Pending = f;
            SourcedFromTape = true;
            if (!engaged)
            {
                engaged = true;
                if (TapeEngaged != null) TapeEngaged();
            }
        }

        public static void ReleaseToLive()
        {
            SourcedFromTape = false;
            if (engaged)
            {
                engaged = false;
                if (TapeReleased != null) TapeReleased();
            }
        }

        /// <summary>
        /// Re-read the device (or the game's own input struct) right now. Used by the recorder at the
        /// capture point, because TasInput.Current was committed at tick head - before the sim had
        /// consumed anything - and would therefore store a one-tick-stale frame.
        /// </summary>
        public static TasInputFrame SampleLiveNow()
        {
            if (LiveReader == null) return Current;
            return LiveReader();
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

    // Live device reading lives in TasLiveInput.cs (PC keyboard/mouse, with injectable
    // readers so you can record exactly what your controller consumed).
}
