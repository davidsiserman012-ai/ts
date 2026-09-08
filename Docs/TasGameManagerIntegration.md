# Wiring the TAS tool to *your* GameManager

Read `TasToolPcGuide.md` for the tool itself. This is what your file changes about the design, and
it's all reverse-engineered from the `GameManager` you sent.

## 1. Your input seam already exists, and it isn't the controller

```csharp
InputData inputData = InputManager.i.GetInputData();
...
lookList[i] = inputData.look_x;
```
Every tick, the game funnels player intent through `InputManager.GetInputData()` into an `InputData`
struct, and `RigidbodyFirstPersonController` consumes that. One accessor = one place to tap.

So **don't** patch the controller for input. `Integration/TasInputAdapter.cs` does two things instead:
* record: read the values `InputManager` produced (i.e. what the sim actually consumed, already
  filtered/quantised by your own control-type logic) → that kills the `Input.GetAxis("Mouse X")`
  smoothing-state drift problem for free, because we never re-filter anything;
* replay: write the tape's values into the same struct and let your existing pipeline do the rest.

The tick-boundary alignment works out without offsets: capture happens at the clock's
`+32000` `FixedUpdate` (the tail), the controller reads `InputData` in the `Update` batch of the
same frame, and playback pushes the frame at the `-32000` `FixedUpdate` (the head) of the tick whose
`Update` will consume it. Head→Update→tail→Update: one tick in, one tick out, aligned.

**The one thing that breaks it**: `InputManager.i` also polls the device in its own `Update`, so
during replay whichever of the two writes last wins. `TasInput.TapeEngaged/TapeReleased` therefore
set `controlEnabled = false`, `DisableControls()`, `enabled = false` for the duration — using your
own three switches, which are already exactly right for this. If you ever see "replay runs but the
player drifts toward the mouse", that's it.

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

1. `InputManager.cs` + the `InputData` struct (field names, and whether there's a setter — if not,
   `public void SetInputData(InputData v) { data = v; }` is the whole ask) → I fill in
   `TasInputAdapter.SampleFromGame/ApplyToGame` exactly, including the Tap/Joystick/Hold channels.
2. `RigidbodyFirstPersonController.cs` + `MouseLook` — to confirm where the accumulated stats are
   written (so the snapshot list is complete) and whether `Update` is the right method for the tick
   driver (if your movement is in `FixedUpdate`, `RegisterTickDriver(c, "FixedUpdate")`).
3. `TimeManager.cs` — if you want the HUD timer to rewind with the run (3 lines) and to confirm
   whether it counts scaled time (that decides whether the in-game seconds and the tape's ticks can
   ever agree during slow-mo).

## 9. Order to do this in

1. `TasSuspend` guard in `Prefs` setters first. Before anything else. It's a 5-minute change and
   it's the difference between a practice tool and an economy exploit.
2. Drop in the 3 `Integration/` files + `Tas/`. Run it with `InputManager` still driving input and
   `TasLiveInput`'s fallback reader: you should get a tape that records, saves a `.tas`, and shows
   sane input in the panel.
3. Send me `InputManager.cs`/`InputData` → wire the adapter properly → verify replay at 1x with
   `InputManager` disabled by the tape-engaged hook.
4. Savestate audit: `Shift+1` before a hard strafe, fail it, `1` to reload, succeed it, then check
   `total_carpan`/`max_carpan` in the inspector match what they were at that tick in the first
   attempt. Any mismatch = a field missing from `TasPlayerRunStats`.
5. Only then flip on the replay toggle and compare `tickCount / tickRate` with `m_TimeManager.Seconds`
   for ten runs. If they agree, the leaderboard can start taking times from the tape.
