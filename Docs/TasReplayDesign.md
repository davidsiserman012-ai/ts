# DemoRecorder → native TAS tool: how the current pipeline works and what a replay tape needs instead

## 1. How your existing `DemoRecorder` actually works

Traced end to end from the code:

```
remote config ──► DemoRecorder.cfgEnabled ─┐
                                           ├─► GATE (both must be true, else nothing is recorded)
Network.meta2 == "v" ─► meta2Enabled ──────┘
                                           
GameManager (round start) ─► StartNewRecording()
      │  gates on GameState.raceMode → self-disables
      │  demoTime = 0; frames = new List<DemoFrame>(7200); cap = totalFrameLimit
      │  captures Screen.width/height + controlType (+ appVer)  ← "how to reconstruct the view"
      ▼
Update(), every render frame
      │  fpsTimeTotal += Time.deltaTime
      │  if (fpsTimeTotal < 1/fpsCap) return;      ← sampler, not a sim rate
      │  sample: demoTime, player.position, camEuler.x, playerEuler.y, fpsChar.speed
      ▼
demoData.frames.Add(gp.ShallowCopy());  demoData.time = frames.Count   ← COUNT IS THE CLOCK
      ▼
EndRecording() ─► JsonUtility.ToJson(demoData) ─► leaderboard upload
      ▼
leaderboard client downloads JSON ─► interpolates frames[i], i = t * fpsCap ─► camera + body follow
```

The whole design rests on one trick: **`demoData.time = frames.Count`**. Because frames are
supposed to be evenly spaced at `1/fpsCap`, the *array index* is the timestamp. That is why
there is no per-frame lookup, no keyframe table, and no interpolation state machine — the
playback is `frames[(int)(t * fpsCap)]`, which is elegant and cheap.

Everything else in it is cosmetic metadata: `ReloadPlayerData()` snapshots identity
(nick/flag/avatar/knife/cape/glove/effect/rank + map name) so the leaderboard row can render
the *player model* while the frames only give the *transform*. `screenWidth/Height` +
`controlTypeStr` exist for the same reason: reconstruct the camera and the control scheme on
a different device.

Two things worth naming explicitly, because they decide the TAS design:

* **It records the effect, not the cause.** `GetInputData()` builds a `List<Vector2Int>` of
  touch positions and **nothing calls it**; `record_touches` is never read. The input half of
  your own pipeline is dead code. So the recorder knows *where the player was*, never
  *what the player did*.
* **It cannot be re-simulated.** Nobody can verify that those positions came from legal
  play, because the constraint (inputs → physics → those positions) was thrown away at
  record time. A state tape is forgeable by construction: whoever writes the JSON decides
  where the player was. That is not a bug in your code, it is the definition of the format.

Bugs/gaps I'd fix regardless of the TAS work:

1. `fpsTimeTotal += Time.deltaTime` sampling is **variable-rate**. On a 30 fps device it emits
   1 sample per render frame, so `frames.Count` stops equaling `t * fpsCap` and playback
   drifts/stutters. The frame-index-as-time trick only holds if the sampler is a real clock.
2. `new List<DemoFrame>(7200)` with a cap of 36000 → two reallocations + `ShallowCopy()` per
   frame = GC pressure exactly on the frames where you cannot afford a hitch.
3. `if (frames.Count >= totalFrameLimit) return;` happens *before* `fpsTimeTotal = 0f` and
   silently truncates the run; the client and the server disagree about whether the run is
   complete. Cap hits must be an explicit, reported outcome.
4. `speed` is stored as `int` (sub-m/s precision is gone) and `camEuler.x / playerEuler.y`
   discards roll + yaw coupling — fine for looking at, useless for verifying.
5. `On_Meta2Set` mutates recording state from a network callback, i.e. between physics steps.
   Harmless for a state recorder; **fatal** for a replay recorder, where event *ordering*
   against ticks is part of the run. That is why the TAS code queues it (`TasDeferred`).
6. `fpsCap` is a public inspector field but `fpsTimeLimit` is computed in `Start()` — change
   `fpsCap` at runtime and the sampling rate and `demoData.time` silently disagree.
7. `record_touches` / `gp` (a shared template mutated then copied) are fragile: if
   `ShallowCopy()` ever becomes a no-op or returns `this`, every frame in the list aliases
   the same object and you record a flat line. Struct-array indexing makes that impossible.

## 2. The one decision that changes everything

| | leaderboard demo (yours today) | TAS replay (what you want) |
|---|---|---|
| records | transforms (the *effect*) | inputs per tick (the *cause*) |
| clock | `Time.deltaTime` accumulator | fixed tick: `tick++`, `t = tick/rate` |
| sample unit | render frame | **simulation** tick |
| data/tick | ~7 floats + JSON | 24 packed bytes, fixed size |
| playback | interpolate + move a camera | re-run the real game, feed recorded inputs |
| can scrub/frame-step | only visually | exactly, both directions |
| can verify a run | no | yes — re-sim must reproduce it |
| can edit a run | no | yes (this is what makes it a *tool*) |
| file size, 10 min | ~4 MB JSON | 864 KB raw, ~60–120 KB gzip |

A TAS tape is `inputs(tick)` and nothing else. The positions/camera/splits are *derived*,
which is precisely the property you are asking for: the leaderboard shows a replay, and the
replay is the run.

## 3. The five pieces the TAS version needs that the demo version does not

### 3.1 A clock you own (`TasClock`)
```
tick++ ; simTime = tick * (1/tickRate)
drain deferred events → apply/sample input → step sim → capture/checksum
```
Every sim read of time must go through it. **This is the single most important refactor:**
in gameplay code, `Time.deltaTime` → `TasClock.Delta`, `Time.time` → `TasClock.Time_s`,
`Time.frameCount`/`Time.unscaledTime` → `TasClock.Tick`. Without it there is no such thing as
"frame 4213" of a run, so there is no tool.

Why `Time.deltaTime` is poison even though it *looks* fine: `deltaTime` is whatever the frame
took. Record on a 120 Hz flagship, replay on a 30 Hz budget phone, and every ballistic arc,
jump apex and slide distance is different — the tape "works" and the run is 0.4 s slower.
`Delta` is constant by definition (`1/tickRate`), so the same tape is the same run.

Three cadences, deliberately (`TasClockMode`):
* **Mode A — `auto`**: engine keeps running its own `FixedUpdate`, we only swap the input
  source and pin `Time.fixedDeltaTime = 1/tickRate`. Zero changes to your movement code beyond
  the `Delta` substitution. Use it for in-game realtime replay + recording.
* **`Hold`**: the engine still runs, but the clock decides per render frame whether a tick is
  allowed, and undoes a non-tick frame by restoring the newest savestate in `LateUpdate`. That is
  slow-mo, pause and frame-advance with no patches and no `timeScale` games.
* **`Manual`** (Mode B): `Time.timeScale = 0` + `Physics.simulationMode = Manual`
  (`Physics.autoSimulation = false` pre-2022.2) and *we* call `Physics.Simulate(dt)` per tick, with
  sim scripts driven by the tick bus (`TasSimBehaviour`, or `TasTickDriver` for scripts you won't
  edit). This is the only mode that can do >1x turbo and headless verification.

### 3.2 One input seam (`TasInput`)
Every read of player input in the game must come from one place. That place has two providers:
live device (record) and tape (playback). The sim cannot tell which is feeding it — that is the
whole trick, and it is why an "in-game native TAS tool" is possible at all in an engine where
`Input.Get*` is scattered across hundreds of call sites.

Record **held state**, not key events. `GetKeyDown` semantics depend on when the OS delivered
the event relative to the tick, so events are not replayable. A bitmask per tick is replayable
by construction, and edges (`JumpPressed`) are *derived* from the previous tick's held bits —
which is why the recorder recomputes those two bits at capture time and the editor recomputes
them after any hand-edit.

Fixed-point, because float text is where runs go to die: movement axes as `short` at ×1000,
look deltas as `int` at ×1000, aim pointer as normalized `ushort` (so `screenWidth/Height`
disappear from the format entirely — a 2432×1080 run replays on a 1080×1920 device).

**Your integration point, as it turns out, was already there** — see
[`TasGameManagerIntegration.md`](TasGameManagerIntegration.md) §1 for the `InputManager.GetInputData()`
version and the `OnTickCapture` timing fix. The general rule, kept below because it is the rule:
 `UnityStandardAssets.Characters.FirstPerson
.RigidbodyFirstPersonController` reads input in exactly two places:

```diff
 void UpdateMovement()
 {
-    Vector2 moveInput = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
+    Vector2 moveInput = TasInput.MoveAxis();          // live or tape, same call
     ...
 }
 void UpdateLook()
 {
-    m_MouseLook.CalculateMouseDelta();               // Input.GetMouseButton / Mouse X/Y
+    input.move = TasInput.MoveAxis();
+    input.look = TasInput.LookDelta();               // degrees this tick, already quantized
 }
```
Everything downstream (velocity, grounded checks, jump, slide) stays untouched, and is now
replayable. If your mobile controller is a custom class, the same patch goes wherever
`Input.GetTouch` is turned into a move vector + look delta — grep for `Input.` in your
`FirstPerson`/`TouchControl` scripts; the count of sites you have to change is the honest
measure of the refactor, and it is small for a movement-driven FPS.

### 3.3 A tape with fixed-size records (`TasTape`)
```
TAST | ver | tickRate | tickCount | checkpointEvery | mapHash | configHash | seed
     | gameVersion, nick, mapname, controlTypeStr, rankStr, metaJson | ids... | runSeconds
     | n | n × 24B input frames | m | m × 40B checkpoints | framesHash
```
Fixed-size ⇒ **O(1) seek to any tick**, which is what makes scrubbing instant. I deliberately
did *not* add RLE/varint compression to the frame block: it would cut ~40 % of 864 KB and cost
you random access. Compress for the wire (`GZip` in `TasDemoExport`), keep it uncompressed on
disk and in memory for the tool.

`configHash` is the load-bearing field for a leaderboard. It is a hash of everything that gives
a tick its meaning: `fixedDeltaTime`, gravity, `bounceThreshold`,
`defaultContactOffset`, `sleepThreshold`, `queriesHitTriggers`, `autoSyncTransforms`, screen size, look sensitivity,
`Application.version`. Playback refuses a tape whose `configHash` differs from the build.
Without that you will get "the replay desyncs at 0:07" tickets forever, because a Unity patch
changed a physics default and nobody can tell why.

### 3.4 Savestates (`TasSnapshot`) — the part emulators get free
An emulator's whole state is one byte array, so rollback is trivial. Unity has no "state":
it is a scene graph + a native physics black box + hundreds of script fields. So you assemble it:

```
snapshot = tick, simTime, TasSim.Rng.s, TasSim.Accumulator,
           [Transform+Rigidbody] × registered bodies,
           [ITasSnapshotable] × registered gameplay state (weapon, ammo, cooldowns, flags)
```
Keyframe every `checkpointEvery` ticks. Scrub-to-tick = restore nearest keyframe + re-sim the
remainder (≤ `checkpointEvery` ticks). That's a codec: sparse big snapshots + cheap re-sim, and
the spacing is your one tuning knob (space ↔ time). `ITasSnapshotable` is also where you keep
honest: every gameplay system that keeps state you *don't* snapshot is a system whose TAS
rollback will diverge, so the list of implementors is literally your determinism audit surface.

### 3.5 Divergence detection (the honest part)
Same inputs + same tick rate does **not** guarantee bit-identical floats across IL2CPP ARM64 vs
x64, or across Unity patch versions. `Mathf.Sin`, quaternion normalization, solver iteration
order, `Random.insideUnitCircle` in a VFX that feeds gameplay, `Dictionary` iteration order,
script execution order, an `AsyncOperation` completing between ticks — all shift a trajectory
by 1e-7 and a wall-bounce clip amplifies it. On a speedrun leaderboard, 1e-7 that turns into
"saved 0.11 s" is a scandal.

So the tape carries sparse state checkpoints + a rolling `stateHash`, and playback has three modes:

| mode | what it does | who uses it |
|---|---|---|
| `SpectateState` | no re-sim; interpolate checkpoints, drive a rig. Your **current** player, unchanged cost, works on every device | leaderboards |
| `ResimLive` | re-sim from inputs; if `‖sim − checkpoint‖ > tol` **snap and count a correction** | TAS tool playback, "drive it myself" |
| `ResimVerify` | re-sim, no correction, no rendering; `divergences == 0` **is the verdict** | verify farm / same-device class |

That split is the answer to "how do I do the same thing for replays in realtime": the
realtime-visible replay is `SpectateState` (cheap, robust, identical to what you ship today),
and the *tool* is `ResimLive`/`ResimVerify`. Same file, same tape, and the leaderboard JSON is
exported from the tape so nothing downstream changes.

## 4. Field-by-field migration

| yours (`DemoData`) | TAS equivalent | note |
|---|---|---|
| `frames: List<DemoFrame>` | `frames: TasInputFrame[]` | 24 B fixed; struct array, no per-tick alloc |
| `demoTime` / `time = Count` | `tick` / `tickCount` | real clock, not an accumulator |
| `fpsCap` | `tickRate` in header | sim rate = sample rate, no longer a "cap" |
| `totalFrameLimit` | `maxTicks` → `endedByCap` | explicit outcome, reported |
| `gp` + `ShallowCopy()` | array index | aliasing impossible |
| `GetInputData()` (dead) | `TasLiveInput.Read()` | the payload |
| `record_touches` | `pointerCount`, `aimX/Y` | always on, cheap |
| `screenWidth/Height` | — (normalized `ushort` aim) | resolution-independent now |
| `speed` (int) | derived on re-sim | not stored; exact, and verifiable |
| `controlTypeStr`/`controlType` | header | kept |
| identity (nick/flag/…/rankIdx) | header | kept |
| `appVer` | `gameVersion` + `configHash` + `mapHash` | version string alone is not enough |
| `JsonUtility.ToJson(demoData)` | `TasTape.Write` binary; `TasDemoExport.ToJson` for the old viewer | both, from one source |
| `Network.OnMeta2Set` gate | same gate, routed via `TasDeferred` | now replay-safe |

## 5. What to check before you trust a tape (server side, no Unity needed)

1. `configHash` + `mapHash` + `gameVersion` ∈ allow-list; reject mismatch rather than replay it.
2. `tickCount * (1/tickRate) == runSeconds`, `tickCount` matches the frame block length,
   `framesHash` matches.
3. Structural: axis magnitudes ≤ 1, no NaN/Inf in checkpoints, no checkpoint-to-checkpoint speed
   spike above your fastest legitimate movement source (sprint+jump+ramp+boost — put the real
   number in config, not the 12 m/s placeholder in `TasToolWindow.Verify`).
4. `GetButton*` rates: >N presses/sec for a touch control scheme is a macro/autopaste tell.
5. Only then spend a verify slot: `ResimVerify`, expect `divergences == 0`, publish the *re-simmed*
   split times. **Never store a client-reported time**; `runSeconds` is derived from the tape.

## 6. Determinism hazard checklist, ordered by how sure I am it will bite you

1. `Time.deltaTime` / `Time.time` / `Time.unscaledTime` / `Time.frameCount` in gameplay code → `TasClock`.
2. `Time.timeScale` used by pause menus/pause-screenshots → it changes your tick cadence.
3. `Random.value`, `Random.insideUnitCircle`, `Random.Range` → `TasSim.Rng` (seeded, snapshotable).
4. Anything `async`/callback-driven that mutates gameplay (`Network.OnMeta2Set` is the example)
   → `TasDeferred.Post` at tick head.
5. `Physics.autoSimulation` / `autoSyncTransforms` / rigidbody `interpolation != None` /
   extrapolation → pinned in the config hash and set explicitly during playback.
6. Gameplay logic living in `Update()` (render-rate) instead of the tick → move to
   `TasSimBehaviour.Tick`, or you get the 30 fps bug from §1 in a new costume.
7. Script execution order (nothing enforces it; `DefaultExecutionOrder` on clock/-32000,
   playback/-31000, recorder/+32000, tail/+32000 is the contract).
8. `Dictionary`/`HashSet` iteration order feeding anything that moves a body.
9. `Invoke`/`Coroutines`/`WaitForSeconds`/`yield return new WaitForFixedUpdate` for gameplay timing.
10. `Unity.Mathematics.math.sin` / SIMD intrinsics (`fast math`) — differs from `Mathf` per platform.
11. `Camera.aspect`, DPI, `Screen.dpi`, canvas scaler in anything that turns a touch into a ray.
12. Object pool order + `Instantiate` order (a pooled VFX with a gameplay collider is a state machine).
13. IL2CPP `float` contraction and `Math.Round` banker's rounding if you ever round a split time.

You do not have to fix all 13 to ship. You have to fix 1–7 to make `ResimLive` correct on the
same device class, and 8–13 to make `ResimVerify` pass on a different CPU. Everything past that
is covered by checkpoint correction, which is why the checkpoints stay.

## 7. Phased plan (each phase ships value alone)

| phase | work | you get |
|---|---|---|
| 0 | `TasClock` + `Delta`/`Time_s` substitution, `TasInput` façade, tape recorder alongside `DemoRecorder` | fixed-rate sampling bug gone; input captured (your dead `GetInputData` becomes real) |
| 1 | binary tape + `TasDemoExport.ToJson` feeding the *existing* leaderboard viewer | 40× smaller payload, viewer untouched |
| 2 | `SpectateState` playback from checkpoints | realtime replay of any tape, same look as today |
| 3 | `ResimLive` + `TasSnapshot` + overlay | the TAS tool: scrub, frame-step, slow-mo, rollback, hand-edit |
| 4 | `configHash` gating + `ResimVerify` + server pipeline | verifiable runs; state-tape spoofing closed |

## 8. Files in this branch

Runtime behaviour of the tool itself (savestates / slow-mo / auto-record / replay toggle /
branch splicing) is documented in [`TasToolPcGuide.md`](TasToolPcGuide.md).

```
Tas/Runtime/TasClock.cs        tick clock, 3 cadences (Auto/Hold/Manual), sim bus, TasSimBehaviour
Tas/Runtime/TasTool.cs         the tool: auto-record start/stop, replay toggle, hotkeys, rollback+splice
Tas/Runtime/TasToolConfig.cs   persisted settings (toggles, tickRate, speed, slots, keys)
Tas/Runtime/TasSavestates.cs   per-tick snapshot ring (zero alloc) + 10 disk slots + visual reset
Tas/Runtime/TasBranch.cs       discarded attempts: cut / list / splice back into the trunk
Tas/Runtime/TasTickDriver.cs   drives uncooperative sim scripts in Manual mode
Tas/Runtime/TasToolUI.cs       the in-build panel (IMGUI): toggles, transport, slots, branches
Tas/Runtime/TasLiveInput.cs    PC keyboard/mouse reader with injectable Move/Look readers
Tas/Runtime/TasClockTail.cs    runs the tick tail at +32000 so capture happens after the sim
Tas/Runtime/TasInput.cs        TasInput façade + TasLiveInput (⚠ adapt axis/button names)
Tas/Runtime/TasInputFrame.cs   the 24-byte record, fixed-point, edges derived
Tas/Runtime/TasDeferred.cs     off-tick events stamped onto ticks
Tas/Runtime/TasRng.cs          owned xorshift64*, seed in the header
Tas/Runtime/TasTape.cs         binary format, checkpoints, configHash, state sampling
Tas/Runtime/TasPhysics.cs      Physics sim-mode shim + rigidbody capture/restore
Tas/Runtime/TasRecorder.cs     drop-in DemoRecorder replacement, same gates & API
Tas/Runtime/TasPlayback.cs     SpectateState / ResimLive / ResimVerify + correction
Tas/Runtime/TasSnapshot.cs     savestates, keyframes, sidecar persistence
Tas/Runtime/TasDemoExport.cs   DemoData-compatible JSON, gzip wire format, upload payload
Tas/Runtime/TasOverlay.cs      HUD: tick clock, input display, desync readout (keys live in TasTool)
Tas/Editor/TasToolWindow.cs    Window ▸ TAS Tool: transport, input grid editor, verify, export
Integration/*.cs               game-side bridge: InputManager seam, run-state snapshots, persistence gate
```
`Tas/Runtime` and `Tas/Editor` are separate asmdefs so `UnityEditor` never reaches a player
build, and the recorder keeps **no** compile-time reference to your game types (delegates, not
`GameState`/`Prefs`/`InventoryHelpers`) — paste the wiring lines, don't add a dependency.
The ⚠ marks are the only places you must edit for your project; everything else is generic.
Nothing here was compiled (no Unity toolchain in this sandbox): expect small signature fixes
against your Unity version, and start with phase 0 on one map.
