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
            if (BlockLiveInput) { Current = default(TasInputFrame); Previous = Current; return; }
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

    // Live device reading lives in TasLiveInput.cs (PC keyboard/mouse, with injectable
    // readers so you can record exactly what your controller consumed).
}
