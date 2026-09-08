# PC native TAS tool: savestates, slow-mo, auto-record, replay toggle

This is the usage/behaviour guide for `Tas/Runtime`. The *why* (input tape vs state tape) is in
[`TasReplayDesign.md`](TasReplayDesign.md). Nothing here is compiled — there is no Unity
toolchain in this sandbox — so expect small signature fixes against your Unity version.

## Quickstart (3 steps, no scene edits)

> If you are on the game codebase that has `GameManager`/`InputManager`, step 2 is already done for
> you: `Integration/TasGameBridge.cs` + `TasUnityBridge.cs` + `TasSuspend.cs`
> subscribe to the events your code already fires (`GameState.gameStarted`, `GameManager.OnOyuncuDustu`,
> `GameManager.OnLevelCompleted`) and tap input at `InputManager.GetInputData()`. Read
> [`TasGameManagerIntegration.md`](TasGameManagerIntegration.md) instead of the two lines below.


1. Drop `Tas/` in `Assets/`. `TasTool` self-installs via
   `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]` and
   `DontDestroyOnLoad`, and creates `TasClock / TasRecorder / TasPlayback / TasOverlay / TasToolUI`
   on its own GameObject. You do not add anything to a scene.
2. Two lines in your existing code, right where the demo recorder is already wired (you have
   those call sites — `DemoRecorder.i.StartNewRecording()` / `EndRecording()`):
   ```csharp
   TasTool.NotifyGameStarted();      // alongside your StartNewRecording call
   ...
   TasTool.NotifyMapFinished("levelComplete");   // alongside your EndRecording call
   ```
   (`TasTool` also guesses from scene loads, so it works before you wire anything: any scene
   whose name is not menu/loader/lobby/boot starts the session; going back to a menu stops it.
   Override the guess with `TasTool.IsPlayScene = name => name.StartsWith("level_")`.)
3. In Play mode press **F9** for the panel. Everything else is hotkeys below.

Optional but recommended, one-time:
```csharp
TasTool.RaceModeProbe    = () => GameState.raceMode;              // same gate as DemoRecorder
TasRecorder.i.SpeedReader = () => (int)fpsChar.speed;             // cosmetic, kept for the demo export
TasTool.RegisterTickDriver(fpsChar, "Update");                    // only needed for >1x turbo
TasLiveInput.MoveReader  = () => myMoveVector;                    // see "record what the sim ate"
TasLiveInput.LookReader  = () => myLookDegrees;
TasRecorder.ScreenInchProbe = () => InputManager.i.screenInch;     // if any scalar scales input
TasRecorder.ScreenInchPin   = v => InputManager.i.screenInch = v > 0f ? v : platformValue;
```

## Keys

| key | does |
|---|---|
| `F9` | panel |
| `F10` | record now / stop (same as the auto-record start/stop) |
| `F11` | stop transport, hand the world back to the engine |
| `F12` | **flip the replay toggle** (persisted; every future game start replays) |
| `Backspace` | rollback the last second (`cfg.tickRate` ticks) |
| `.` / `,` | frame advance +1 / -1 (rollback = keyframe rewind + re-sim) |
| `-` / `=` | slow-mo down / up (1/32x .. 8x) |
| `[` | reset to 1x |
| `]` | pause (hold current frame) |
| `1..9`,`0` | savestate slot save |
| `Shift + digit` | load that slot (truncates the trunk, stashes the tail as a branch) |
| `F8` / `Ctrl+F8` | quicksave / quickload in slot 0 |
| `Ctrl+S` | save the current trunk tape to disk |

## What each feature actually does, and its one caveat

### Auto-record from game start
`MarkGameStarted()` → `TasRecorder.StartNewRecording()`, which resets `TasSim.Rng` to the tape's
seed, clears the trunk + ring, and subscribes to the clock tail. Nothing samples in `Update`, so
no missed or doubled frames regardless of your render rate. `cfg.autoRecordOnGameStart` off =
record only when you press F10.

**Caveat, and it is the important one:** for a tape to be replayable, the *simulation* must run
at the tape's tick rate — not the render rate. So while the tool is on, `enforceFixedRate` sets
`Time.captureFramerate = tickRate`, which pins `Time.deltaTime` to exactly `1/tickRate` every
frame. That is what lets your unpatched `RigidbodyFirstPersonController` (which multiplies by
`Time.deltaTime` in several places) record and replay bit-exactly, with **zero edits to it**.
The cost is real and you should know it: in `captureFramerate` mode Unity does **not** catch up,
so if the GPU can't hit 60 the game runs slower than realtime instead of dropping simulation
ticks. For a TAS tool that is the correct trade (frame-exactness over wall-clock), but it will
feel different from normal play on a weak machine, and audio/visual sync can drift. Turn
`enforceFixedRate` off only once every sim script reads `TasClock.Delta`.

### Save states
Two tiers, because they are used differently:
* **Ring** — one snapshot per tick in RAM (`cfg.ringCapacity`, default 1800 = 30 s). Zero
  allocation per tick: the ring writes into a preallocated fixed buffer per slot
  (`TasSnapshot.CaptureInto`), and if a snapshot exceeds `TasSavestates.Stride` it reports
  `OVERFLOW` in the panel rather than silently truncating (a silently truncated savestate is
  how you get "rollback corrupts sometimes"). ~1.5 KB/tick here → ~2.7 MB at the default.
* **Slots** — 10 named snapshots on disk at `persistentDataPath/tas/slot_N.tasst` + `slots.json`.
  These survive restarts, so an all-evening session of retrying one strafe doesn't vanish.

A snapshot = `(tick, simTime, TasSim.Rng, hash accumulator, registered rigidbodies, every
ITasSnapshotable)`. So: implement `ITasSnapshotable` on **weapon/ammo/cooldowns/grenade
timers/checkpoint flags/door state/NPC AI** — anything with state you don't register will be
*stale after a load*, which reads as a broken tool but is a missing registration. That list is
your determinism audit, and it's also exactly the list a cheat would love to leave out.

### Slow-mo, pause and frame-step (the mechanism, because it's unusual)
Unity has no "step one tick". `Time.timeScale = 0` freezes everything including your controller,
and it can't be nudged forward. So `TasClockMode.Hold` does it differently: **each render frame,
we decide whether a tick is allowed. On frames where it isn't, the engine has still simulated —
so we re-apply the newest savestate in `LateUpdate`, which is after all simulation and before
rendering. The held frame never reaches the screen.**

* `0.25x` = advance 1 tick every 4 frames (motion is stepped at 15 updates/s — that is what
  slow-mo looks like in every TAS tool, and it is honest: the sim never half-ticks).
* pause = never advance; `.` = advance exactly one.
* `>1x` turbo can't use this path (you can't run 4 sim ticks inside one engine frame), so above
  1x the clock switches to `Manual`: `timeScale = 0`, `Physics.Simulate` per tick, and sim
  scripts must be driven by the tick bus. Two ways: `TasSimBehaviour.Tick` for your own scripts
  (correct, permanent), or `TasTool.RegisterTickDriver(fpsChar, "Update")` — `TasTickDriver`
  disables the behaviour and invokes its `Update` once per tick by reflection, no source edits.
  Under IL2CPP that reflection target can be stripped: keep it in `link.xml`, or use
  `TasSimBehaviour`. If "playback moves physics but not the player", you are in Manual mode with
  an undriven script — that is the symptom.

### The replay toggle
`cfg.autoReplayOnGameStart` (F12 or the panel checkbox) is persisted in
`persistentDataPath/tas/config.json`. With it on, every game start loads the last run and plays
it back from tick 0 in realtime; `loopReplay` re-arms on finish. Playback at 1x = `Auto` mode:
**the game is running for real, only the input source is the tape** (`TasInput.SourcedFromTape`
+ `BlockLiveInput`), which is the closest thing to "the same thing but replaying" you can get:
same renderer, same physics, same animation, same VFX, and the player is a live player object you
can free-look around with a spectator camera.

`replayMode` picks the strength:
| mode | what it is |
|---|---|
| `SpectateState` | no re-sim: interpolate the tape's checkpoints and drive a rig. Cheapest, works on any device — that's your current leaderboard path |
| `ResimLive` (default) | real re-sim from the inputs; when `‖sim − checkpoint‖ > snapTolerance` it snaps and counts a correction |
| `ResimVerify` | re-sim with no correction, no render; `divergences == 0` **is** the verdict |

### "as if it was all one run" — rollback does not append, it splices
This is the design point your request hinges on, so to be explicit: a TAS run is **not** a
recording of your session. Your session is 40 retries; the run is one contiguous tick range.

So on rollback (or slot load) while recording:
1. `branches.Cut(tick, accumulatorAtThatTick)` — stash `trunk[tick..]` as a selectable branch,
   so a rewound attempt is never destroyed (and can be spliced back if it was the better one);
2. `TasSavestates.TryRollback(tick)` — sim, RNG and hash chain go back to that exact tick;
3. `TasRecorder.TruncateTo(tick, hashAtTick)` — **the trunk's tail is deleted.**
4. You keep playing. The recorder appends from `tick` onward, with the RNG restored, so the new
   input continues the same stream with no seam, no gap, no restart marker.

Result: the trunk *is* the final run, and `TasPlayback` needs no branch-aware code at all — it
just replays ticks 0..N. `branches.Splice(id)` does the other half of the workflow: replace
`trunk[cut..]` with a stashed attempt (trunk = `trunk[0..cut] ++ branch`), still one contiguous
range. `Panel → BRANCHES → splice` is the "attempt 2's strafe was better than attempt 3's" button.

### Files it writes (all `Application.persistentDataPath/tas/`)
```
config.json                 settings + both toggles
nick_map_20260908_2210.tas  a run trunk (864 KB per 10 min, verifiable)
*.branches.bin              that run's discarded attempts
slot_0..9.tasst + slots.json savestate slots
last_run.txt                what the replay toggle loads on next game start
last_demo.json              DemoData-shaped export for the existing leaderboard viewer
library.json                append-only run log: time, ticks, configHash
```

## "Record what the sim ate" — the look-input trap to check first
`Input.GetAxis("Mouse X")` is a *filtered* axis: the Input Manager keeps smoothing/sensitivity
state across frames. If you record the raw device delta and your `MouseLook` re-filters it during
playback, playback starts from a different filter state and the aim drifts a few hundredths of a
degree per tick — invisible until frame 9000, then a clip that either isn't there or shouldn't
be. Two safe answers:
* read the **degrees the controller applied** into `TasLiveInput.LookReader` (default in
  `TasTickDriver`-free setups), or
* force the controller onto `GetAxisRaw` and record that (also fixes `m_MouseLook` smoothing).
`TasLiveInput`'s fallback path is the second style; confirm it matches your file before you trust a
10-minute run. Same reasoning is why the tape stores **held bits** and derives `JumpPressed` /
`FirePressed` from the previous tick, never a `GetKeyDown` moment.

Related, and it is the fix that mattered most in this codebase: **the capture point is `LateUpdate`,
not the tick tail.** If the game writes input and consumes it in the same `Update` batch (yours does),
sampling at a `FixedUpdate` tail stores last frame's value while playback pushes this frame's — a
constant one-tick offset that no amount of tolerance tuning catches because it never diverges. See
`TasClock.OnTickCapture` / §1a of the integration doc.

## First-session checklist
1. `F9`, panel shows `RECORDING from game start`. Play 20 s, `F11`. Console must show
   `EndRecording [TAS] ... N ticks @60Hz = N/60 s` and a `.tas` path.
2. `F12` (replay ON) → restart the game. It should replay in realtime, player untouched,
   `desync 0` in the HUD.
3. Slow to `1/4`, press `]`, then `.` six times: exactly one tick per press, physics + aim intact.
4. `Shift+1` after a bad strafe: rewind, retry, `Panel → Save run`. Trunk length should be
   `~the tick you rewound to + your new ticks` — that truncation is proof the splice works.
5. Then the honest question: does a **fresh process** with `replayMode=ResimLive` show
   `divergences == 0`? If not, the run isn't reproducible yet — go down the hazard list
   (§6 of the design doc), not the playback code.
