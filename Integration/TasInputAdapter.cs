using System;
using Tas;
using UnityEngine;

/// <summary>
/// THE seam. Your game already funnels all player input through one call -
/// GameManager.On_FirstInput does `inputData = InputManager.i.GetInputData()` and reads
/// `inputData.look_x` out of it. That is much better news than "we have to patch the controller":
/// InputManager is the single place the sim gets its hands on the device, so recording and replay
/// both hook here and nothing below it (RigidbodyFirstPersonController, MouseLook, your jump/
/// slide/surf logic) has to change at all.
///
/// Two functions to fill in. They are the only unknown in the whole integration, and they are
/// ~10 lines each. Send me InputManager.cs (and the InputData struct) and I will write them exactly;
/// until then, the fallbacks below sample and re-inject through whatever public surface exists,
/// which is enough to prove the pipeline on keyboard/mouse.
/// </summary>
public static class TasInputAdapter
{
    static InputManager IM { get { return InputManager.i; } }
    static bool bound;

    public static void Bind(GameManager gm)
    {
        if (bound || IM == null) return;
        bound = true;

        // Record path: called by TasLiveInput once per tick, BEFORE the game reads it.
        TasLiveInput.SampleOverride = SampleFromGame;

        // Replay path: called by TasPlayback at each tick head; we write the tape's values into the
        // same place the device writes them, so InputManager does its normal filtering and the
        // controller never learns the difference.
        TasInput.TapeApplier = ApplyToGame;

        // Look/move come from the game's own post-filter values: that is what removes the
        // "GetAxis smoothing state differs between record and replay" drift entirely, because we
        // record the number the sim actually consumed instead of the raw device delta.
        TasLiveInput.LookReader = delegate
        {
            if (IM == null) return Vector2.zero;
            InputData d = IM.GetInputData();
            return new Vector2(d.look_x, d.look_y);
        };
        TasLiveInput.MoveReader = delegate
        {
            if (IM == null) return Vector2.zero;
            InputData d = IM.GetInputData();
            return new Vector2(d.move_x, d.move_y);
        };
        // CRITICAL. During replay, InputManager must stop polling the device or it overwrites the
        // tape's values inside the same Update batch (whichever script runs last wins). You already
        // have the exact knobs for this: controlEnabled + DisableControls().
        TasInput.TapeEngaged += delegate
        {
            if (IM == null) return;
            IM.controlEnabled = false;
            IM.DisableControls();
            IM.enabled = false;
        };
        TasInput.TapeReleased += delegate
        {
            if (IM == null) return;
            IM.enabled = true;
            IM.controlEnabled = true;      // or restore your own rule: = !GameState.raceMode;
            IM.ResetInputData();
        };

        // Cursor lock flips at StartGame (Locked) and at both end events (None). SampleOverride
        // resyncs on the edge, so the flick back to screen centre never enters the tape.
        Debug.Log("[TAS] input adapter bound to InputManager");
    }

    /// <summary>⚠ FILL IN with your real InputData fields (look_x/look_y/move_x/move_y/jump/tap...).</summary>
    static TasInputFrame SampleFromGame()
    {
        TasInputFrame f = TasLiveInput.ReadFallback();
        if (IM == null) return f;

        InputData d = IM.GetInputData();
        f.moveX = TasInputFrame.FromFloat(d.move_x);
        f.moveY = TasInputFrame.FromFloat(d.move_y);
        f.Set(TasButton.Jump, d.jump);                 // ⚠ name
        f.Set(TasButton.Fire, d.fire);                  // ⚠ name
        f.auxA = (short)Mathf.Clamp(Mathf.RoundToInt(d.look_x * TasInputFrame.FP), -32768, 32767);
        f.auxB = (short)Mathf.Clamp(Mathf.RoundToInt(d.look_y * TasInputFrame.FP), -32768, 32767);
        // ⚠ control-type-specific channels: TapMode's queued taps, ControlTypesManager state,
        // the touch button ids. Anything the sim reads per tick belongs in the tape, or it cannot
        // be reproduced - that is the entire difference between a demo and a TAS.
        return f;
    }

    /// <summary>⚠ FILL IN: write the frame back so InputManager.GetInputData() returns it.</summary>
    static void ApplyToGame(TasInputFrame f)
    {
        if (IM == null) return;
        InputData d = IM.GetInputData();
        d.move_x = f.moveX / (float)TasInputFrame.FP;
        d.move_y = f.moveY / (float)TasInputFrame.FP;
        d.jump = f.Has(TasButton.Jump);
        d.fire = f.Has(TasButton.Fire);
        d.look_x = f.auxA / (float)TasInputFrame.FP;
        d.look_y = f.auxB / (float)TasInputFrame.FP;
        IM.SetInputData(d);         // ⚠ if there is no setter, add one: `public void SetInputData(InputData v) { data = v; }`
    }
}
