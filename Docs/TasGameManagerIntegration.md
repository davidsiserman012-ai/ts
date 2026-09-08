# Wiring the TAS tool to *your* GameManager

Read `TasToolPcGuide.md` for the tool itself. This is what your file changes about the design, and
it's all reverse-engineered from the `GameManager` you sent.

## 1. The seam is `GetInputData()`, and your short-circuit is the right shape

You already put the hook in `InputManager`:

```csharp
public InputData GetInputData()
{
    if (TasInputBus.Active)                                       // tape is the source of truth
        return TasUnityBridge.BuildInputData(TasInputBus.Current);
    return controlEnabled ? input : emptyInput;                   // live device
}
```
`Integration/TasUnityBridge.cs` (namespace `UnityTAS`) is exactly the API that needs to exist for
those two lines to compile — `TasInputBus.Active`, `TasInputBus.Current`, `TasUnityBridge.BuildInputData`.
Nothing below `InputManager` is touched: `MouseLook`, `RigidbodyFirstPersonController`, the jump/surf
code all keep reading their own struct.

This is better than the "disable InputManager during replay" I told you last round, and I've removed
that: your live path can keep running, so `t_delta_l_sum`, `mpos_delta` and the cursor bookkeeping
stay warm and nothing jumps the moment replay releases. What replay *does* need is
`ResetVars()+ResetInputData()` on both edges (done in `TasInputAdapter`), or the first live frame
after a replay inherits the accumulator state from before it.

### 1a. The off-by-one this file exposed (fixed)

Your game **writes** input in `InputManager.Update()` and **consumes** it in the controller's
`Update()` — same batch, no boundary in between. My recorder captured at the tick tail, which is a
`FixedUpdate` callback and therefore *before* that Update batch: it would have stored last frame's
`look_x`. Meanwhile playback pushes at the tick head, so the sim got each input one frame later than
it got it while recording. Constant, so it never explodes — it just never matches, and one frame of
error during a fast strafe is already past any correction tolerance. Fixed:

* `TasClock.OnTickCapture` now fires from `LateUpdate` (after every gameplay `Update` consumed this
  tick's input, before the frame is drawn) instead of the `+32000` `FixedUpdate` tail.
* The recorder samples **fresh** there (`TasInput.SampleLiveNow()` → your `GetInputData()`), rather
  than reusing the value committed at head.
* `TasPlayback` advances/compares on the same event, so both paths define "tick N" as "what the sim
  saw during frame N". Aligned, zero offset.
* In `Manual` mode (turbo/verify) there is no Update batch, so `Step()` invokes the capture inline
  after `Physics.Simulate` — each mode is self-consistent, which is all a tape requires.

It also drains a *count* of pending captures per frame, so if Unity ever runs 2 `FixedUpdate`s in one
frame you get 2 captures instead of a dropped one. With `enforceFixedRate` it's exactly 1:1 — one of
the reasons that flag exists.

### 1b. Four fields your `InputData` forced into the design

1. **`input.direction` is derived, so it must be re-derived.** `CalculateDirection()` runs in
   `Update()` over the *live* bools; during replay `GetInputData()` short-circuits past it. If
   `BuildInputData` didn't recompute `direction` from the fed bits, the run would move in whatever
   direction your real last keypress implies. It recomputes it, with the same six-term sum, in the
   same order — `Vector3` addition order is not associative, so `dir += left; dir += right;` in a
   different sequence is a different float.
2. **`input.inputLastFrame` must be stamped every call.** Anything that treats `!= Time.frameCount`
   as "stale" would ignore the tape wholesale. `BuildInputData` sets `Time.frameCount`.
3. **The six bools are the movement input, not buttons.** `KeyboardSupport`/`PointerDown` set
   `left/right/fwd/bck/up/down`, so the tape got six new held-state bits (`DirLeft`…`DirDown`,
   `TasButton` bits 17-22) instead of pretending there's an axis. `analog_x/y` (the thumb stick) stay
   as the ×1000 fixed-point pair.
4. **`screenInch` scales movement and comes from `Screen.dpi`.**
   `analogX = screenInch * (t_delta_l_sum.x / Screen.width) * 3f` means the same swipe is a different
   speed on a different display, so it now lives in the tape header, is mixed into `configHash`
   (along with `Screen.height` and `Screen.dpi`), and is **re-pinned during replay**
   (`TasUnityBridge.PinScreenInch`) so a run recorded on your dev machine verifies anywhere.
   `look_x` sidesteps the problem: `dtl4/4` from `Input.GetAxis("Mouse X")` is a pixel delta, and
   because we record `input.look_x` (post-filter) rather than the device delta, the axis's internal
   smoothing state can't desync the replay — that specific hazard in your file is why the
   "record what the sim ate" rule exists at all.

### 1c. Things in this file to fix independently of the TAS tool

* **`using UnityEditor;` in a runtime script.** Nothing in the file uses it; in a player build the
  editor assembly isn't referenced, so this is a compile error (or a link warning at best) for every
  platform. Delete the line — and note `InputManager` is the file your *players* run, so it must stay
  editor-free. `using System;`/`using System.Collections.Generic;` and `using UnityTAS;` are unused
  too (`UnityTAS` only becomes used by the bridge).
* **`FirstInputRoutine` captures `inputData` once, outside the loop.** `lookList[i] = inputData.look_x`
  therefore writes the same value ten times — `inputData` is a snapshot taken before the routine's
  first `WaitForFixedUpdate`, and only aliases `InputManager.input` if `InputData` is a *class*. So: is
  it a struct or a class? If it's a struct, `speedList`/`lookList` analytics are wrong today; if it's
  a class, the loop accidentally works. Same question decides whether `BuildInputData` should mutate
  `im.input` (right for both, which is why it does) and whether `emptyInput` can be polluted by anyone
  holding the reference.
* **A press+release inside one frame is silently lost.** `PointerDown("jump")` then `PointerUp("jump")`
  before `UpdateControls` runs leaves `input.jump == false`: the sim literally cannot see it. That's
  the correct semantics for a *held-state* tape (what the sim can see is the only thing worth recording),
  and it means a one-tick tap **is** authorable — a tape frame with `Jump` set and the next frame clear
  survives, because the writer is `BuildInputData`, not the UI. If you ever want real mid-frame taps,
  you'd need an edge accumulator (`jumpQueued` consumed-and-cleared by the sim) — that's a gameplay
  change, not a tool change, but it's what "1-frame jump trick" requires on touch.
* `Input.simulateMouseWithTouches = false` (set in `Awake`) is what keeps the `mpos_delta` fallback and
  the right-half touch branch from double-counting. The tool relies on that invariant; if it ever moves
  behind an option, `look` becomes non-deterministic.

## 2. Hooks: zero edits, one optional line

| signal in your code | what the tool does |
|---|---|
| `GameState.gameStarted` rising edge (set in `StartGame`) | `MarkGameStarted()` → auto-record, or auto-replay if the toggle is on |
| `GameState.gameStarted` falling edge | stop + save the run |
| `GameManager.OnOyuncuDustu` (static event) | `MarkFinish("death")` + `NotifyMapFinished` + flush deferred writes |
| `GameManager.OnLevelCompleted` (static event) | `MarkFinish("level")` + stop |
| `Prefs.Golge`, `GameState.raceMode`, `m_TimeManager.Seconds` | config hash extras, race-mode gate, authoritative game seconds |

`TasGameBridge` subscribes/polls from its own GameObject (`RuntimeInitializeOnLoadMethod`), so
`GameManager.cs` stays byte-identical. Polling on a 0.05 s edge instead of calling from `StartGame`
is deliberate, and it matters: `StartGame` sets `gameStarted = true` *in the middle*, then finishes
with

```csharp
m_playerRigidBody.isKinematic = false;
m_playerRigidBody.velocity = Vector3.zero;
m_playerRigidBody.angularVelocity = Vector3.zero;
```
Tick 0's snapshot must be taken **after** that kinematic dance, or the first savestate of every run
has the wrong body state and the first retry after a rollback starts from a different physics
situation than the run did. Your `DemoRecorder.StartNewRecording()` is the last statement in the
method for the same reason — if you do wire it inline, put it in that same spot.

**Optional, for frame-exact finish:** `OnLevelCompleted` is raised by
`Invoke("LevelCompletedEvent", 0.1f)` inside `Olay_OyuncuDustu`, i.e. ~6 ticks after the real moment
and only *if the player fell*. The actual instant is `Olay_LevelTamamlandi`, which sets
`b_levelTamamlandi = true` and has no event. Add one line there for exact stamps:
```csharp
public void Olay_LevelTamamlandi(object gonderen, object veri) { TasTool.NotifyFinishExact("line"); ... }
```

## 3. Your demo stop is on the wrong event, and the tape tells you

`DemoRecorder.i.EndRecording()` is called in `Olay_OyuncuDustu`, **not** in `Olay_LevelTamamlandi`.
So for a completed map the recording keeps sampling through the score panel, the interstitial ad,
`baslangicAyarlama()` and the respawn — until the next fall. Your leaderboard demos contain that
wandering today. The tape fixes it as a side effect: `MarkFinish` stamps `finishTick` into the
header, `cfg.trimToFinishTick` cuts the trunk there on stop, and the export writes frames only up to
the line. Result: shorter payload, a run time that equals the moment the player touched the finish,
and a replay that ends where the run ended. `gameRunSeconds` (your `m_TimeManager.Seconds`) is
recorded alongside so you can see the disagreement between "the game's timer" (integer seconds) and
"the tape" (`tickCount / tickRate`) — when they differ, the tape is right and your leaderboard is
rounding.

## 4. Run state that must live in a savestate (found by reading your resets)

`StartGame` zeroes ten fields on the controller:

```csharp
total_speed, total_carpan, max_carpan1, max_carpan2, max_speed,
max_carpan, total_frame, total_frame_carpan, max_yfactor, total_force
```

That list is the tell: the game itself treats them as per-run. `total_carpan`/`max_carpan*` are the
carry multipliers → they feed jump force → they feed physics. If a rollback rewinds the position but
keeps post-crash carry stats, your retry has different physics from the attempt it replaced, and
the spliced trunk no longer reproduces itself — which is *exactly* the "worked in the editor, broke
in the build" class of bug. So `TasPlayerRunStats : MonoBehaviour, ITasSnapshotable` (attached by
the bridge to the player) snapshots those ten plus `MaxHizY`, `m_Jump`, and the `GameState` flags
that change behaviour (`TapMode`, `SurfSliding`, `b_levelTamamlandi`, `DieOnLevelComplete`,
`b_casualBaslangicAyarlandi`, `JumpForceCarpan`, `LastPlane`), plus `ScoreCollision.totalScoreCollision`,
`LevelSonu.KaydedilenPuan` and `m_ScoreManager.Score`.

**Anything you leave out of that list is stale after a load.** Work through it once per system
(weapon/knife `KnifeController.Yenile()` state, `ControlTypesManager`, spawn/`baslangicAyarlama`
bookkeeping, `TutorialHintManager.hintShown`, ad/cooldown timers) and each one is either "give it an
`ITasSnapshotable`" or "prove it's cosmetic". `Rigidbody.velocity/velXZ` are deliberately *not* in
there — the ring's `TasPhysics.Restore` owns them, and two writers of one quantity is how you get a
half-applied state.

## 5. The exploit you have to close before the tool is public

`Olay_OyuncuDustu` writes, in one call: `Prefs.GamesWon`, `QuestDailyGameCount/Score/PlayTime/
LevelComplete`, `BoostCaseTimer` (+`BoostCaseCount` handout), `LuckyWheelTimer`,
`GameManagerHelpers.ScoreRegister(score, seconds)`, `InventoryHelpers.Coin += bpInt`, and
`CachedPrefs` `(PInt)14/15/16`. Every one of them is triggered by in-game state a player controls.
Give a player a savestate and a rollback and you have given them: *hit score box → load slot →
hit score box → …* = unlimited coins, quest completions, boost cases, rank points and leaderboard
entries. That's not theoretical; the loop is ~2 seconds long.

`Integration/TasSuspend.cs` is the gate: while a TAS run is live, `PersistenceAllowed` is false and
your writes are buffered, then flushed **once** at run end, for the final trunk only. Put the guard
in your `Prefs`/`CachedPrefs` setters (best — one place, can't be forgotten) or at the call sites in
`Olay_OyuncuDustu` (fastest). Same rule for `ScoreRegister`: never let a rewound attempt register a
time.

Related: `Olay_CarpismaSkorKutusu` uses `UnityEngine.Random.Range(0.95f, 1.05f)` for the payout.
It's the global RNG, so it's unreproducible and unrestorable; the payout is economy (see above) not
physics, so the *replay* is fine — but `TasSim.Rng` is the one to use if you ever make score depend
on the roll. Your commented-out `RandomizePlayerLocation` is the more dangerous version of the same
pattern: a random spawn offset per run makes every tape start from a different position. Keep it off
while the tool is in use, or seed it from `TasSim.Rng` at tick 0.

## 6. Things your file told me about slow-mo specifically

* `Invoke("syncanim", 0.5f)`, `Invoke("Rank3dClose", 8f)`, `Invoke("LevelCompletedEvent", 0.1f)`,
  `InvokeRepeating("say_saniye", 1f, 1f)` and the `WaitForFixedUpdate` loop in `FirstInputRoutine`
  all run on Unity's clock, which `Hold` mode cannot pause. `Time.captureFramerate = tickRate` keeps
  `Time.deltaTime` pinned, so during `0.25x` slow-mo those timers advance 4× **relative to the
  simulation**. For you the affected ones are UI/animation, so it's cosmetic — but it's why the tool
  is honest about it: **slow-mo is for practising and reviewing, not for recording.** Guard anything
  that matters with `TasClock.CancelledFrame` (one `if` per site) if you want it frozen in holds.
* `Network.SyncAnims()` runs inside `Invoke("syncanim")` and again at the end of `Olay_OyuncuDustu`.
  Network-driven state landing mid-tick is a desync generator; `TasDeferred.Post` is the pattern
  (queue with the tick it should apply on, drain at tick head). Fine for a single-player TAS run,
  not fine if you ever verify a run that interacted with the server.
* `GameState.ReklamInterstitial.ReklamiGoster()` on death can background the app → a multi-second
  frame gap. Under `enforceFixedRate` the sim doesn't catch up, so the tape is unaffected; if you
  ever turn that off, an ad can inject one enormous `deltaTime` into the middle of a recorded run and
  nothing downstream can tell. `TasClock.slowFrames` counts those frames so you can see it happen.

## 7. Surf/physics knobs that belong in the config hash (and now are)

`GravityAyarla()` sets `-9.81*0.25` for `OyunMode.Surf` and `-13.5` otherwise, but it only runs from
`Start()` behind `if (!waitreset_onsceneload && (GameState.raceMode || !waitreset_onnonracemode_start)) return;`
— so on some paths gravity/`Baglantilar()` haven't happened yet when a session boots. Hence: the
bridge re-binds every 0.05 s until `gm.m_playerController` exists, and `ComputeConfigHash()` runs at
`StartNewRecording` (after `StartGame`), not at boot. `TasRecorder.ExtraConfigProbe` adds
`Prefs.ControlType`, `TapMode`, `SurfSliding`, `Physics.gravity`, `JumpForceCarpan`, `MaxHizY`,
`ActiveBoost`, `oyunMode` and `sceneName`. A tape whose hash doesn't match the current build is
refused rather than replayed wrong — with gravity per map mode and a carry multiplier feeding jump
force, refusing is the only sane behaviour.

## 8. What I still need from you

1. `InputData` itself (the struct/class definition) — one word answer needed: **is it a `struct` or a
   `class`?** It changes `FirstInputRoutine`, `emptyInput` safety, and whether `im.input = d;` at the
   end of `BuildInputData` is a copy or a shared write. Also whether `ResetInput()` clears
   `direction`/`inputLastFrame` too.
2. `RigidbodyFirstPersonController.cs` + `MouseLook` — to confirm where the accumulated stats are
   written (so the snapshot list is complete) and whether `Update` is the right method for the tick
   driver (if your movement is in `FixedUpdate`, `RegisterTickDriver(c, "FixedUpdate")`).
   `RigidbodyFirstPersonController`'s `Update` vs `FixedUpdate` also decides
   `RegisterTickDriver(c, "...")`, and I still need to see where the `total_*`/`max_carpan*` fields are
   written to be sure the snapshot list is complete.
3. `TimeManager.cs` — if you want the HUD timer to rewind with the run (3 lines) and to confirm
   whether it counts scaled time (that decides whether the in-game seconds and the tape's ticks can
   ever agree during slow-mo).
4. `ControlTypesManager` — it decides whether `TapMode` changes the *meaning* of the six bools
   (`PointerDown` sets them directly, `UpdateControls` derives `analog_x/y` from touch). If the
   control type can change mid-run (options menu), it must be re-hashed per tick or pinned.

## 9. Order to do this in

1. `TasSuspend` guard in `Prefs` setters first. Before anything else. It's a 5-minute change and
   it's the difference between a practice tool and an economy exploit.
2. Drop in the 3 `Integration/` files + `Tas/`. Run it with `InputManager` still driving input and
   the bridge's `SampleOverride` already reading your real `InputData`: you should get a tape that
   records, saves a `.tas`, and shows sane input/look numbers in the panel.
3. Verify replay at 1x: `TasInputBus.Active` must be `true` only while playing, and the panel's
   `look`/`mv` readout must match the numbers in `input` during playback.
4. Savestate audit: `Shift+1` before a hard strafe, fail it, `1` to reload, succeed it, then check
   `total_carpan`/`max_carpan` in the inspector match what they were at that tick in the first
   attempt. Any mismatch = a field missing from `TasPlayerRunStats`.
5. Only then flip on the replay toggle and compare `tickCount / tickRate` with `m_TimeManager.Seconds`
   for ten runs. If they agree, the leaderboard can start taking times from the tape.
