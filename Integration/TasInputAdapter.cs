using System;
using Tas;
using UnityTAS;
using UnityEngine;

/// <summary>
/// The game-side half of the input seam. Two directions, both through InputManager:
///
///   record   InputManager.input  --FromInputData--> TasInputFrame  --tape-->
///   replay   tape --BuildInputData--> InputManager.input  (short-circuited by GetInputData)
///
/// Nothing below InputManager is touched: MouseLook, RigidbodyFirstPersonController, the jump/surf/
/// slide code all keep reading their own struct. That is what makes a native TAS tool viable in a
/// codebase this size - one accessor in, one accessor out.
/// </summary>
public static class TasInputAdapter
{
    static bool bound;

    public static void Bind(GameManager gm)
    {
        if (bound || InputManager.i == null) return;
        bound = true;

        // Recording: sample the game's own post-filter values at the clock's capture point
        // (LateUpdate, after InputManager.Update wrote them and after the controller consumed them).
        TasLiveInput.SampleOverride = TasInputBus.SampleLive;

        // Replay: every fed tick publishes into the bus that GetInputData() short-circuits on.
        TasInput.TapeApplier = delegate (TasInputFrame f) { TasInputBus.Engage(f); };

        TasInput.TapeEngaged += delegate
        {
            // Do NOT disable InputManager here (that was my earlier advice; your GetInputData
            // short-circuit is better) - keeping it live means its touch accumulators and cursor
            // bookkeeping stay warm, so nothing jumps when replay releases.
            TasUnityBridge.ResetTransient();
            TasUnityBridge.PinScreenInch(TasTool.i != null && TasTool.i.LastTape != null
                                          ? TasTool.i.LastTape.header.screenInch : 0f);
            Debug.Log("[TAS] tape engaged: InputManager.GetInputData() now served from the tape");
        };

        TasInput.TapeReleased += delegate
        {
            TasInputBus.Disengage();
            if (TasTool.i != null) TasTool.i.RestoreScreenInch();
            TasUnityBridge.ResetTransient();
        };

        Debug.Log("[TAS] input adapter bound. InputData: 8 bools + analog_x/y + look_x/y + direction" +
                  " | screenInch=" + TasUnityBridge.CurrentScreenInch.ToString("0.000"));
    }
}
