# Controller audit: what `RigidbodyFirstPersonController` (bhop) means for the tape

Second-hand knowledge of your movement code is what makes rollback tools quietly wrong, so this is
line-by-line. Everything here is already implemented in `Integration/TasUnityBridge.cs`; what needs a
decision from you is in §7 (six small edits) and §8.

`InputData` is a **struct** → confirmed: `emptyInput` can't be polluted by a held reference,
`im.input = d` is a copy write, and `GameManager.FirstInputRoutine`'s `inputData` snapshot taken
*outside* its loop means `lookList[i]` is the same value ten times. That analytics bug is real today;
the TAS tool didn't cause it.

## 1. Headline: `enforceFixedRate` is mandatory for this controller, not a nicety

You already discovered one instance of this and fixed it — the `lookAccum` comment:

> Look movement arrives as a PER-FRAME delta, but the bunny gain is spent once per FixedUpdate …
> Accumulate the delta between physics steps and spend the total.

Three more of the same shape are still live, and they are exactly what a tape cannot express:

1. **`GetInput()` is called twice per frame and has side effects.** `RotateView()` (Update) calls it
   and `FixedUpdate()` calls it again. Each call runs `tap_x = ButonManager.GetTapX();` and writes
   `GameState.TapMode = true/false` and `ControlTypesManager.i.split_inputx/split_inputy`. So the
   game's own idea of "is this a tap run" and the tap queue's consumption count depend on how many
   physics steps the frame ran. At 144 fps with `fixedDeltaTime=1/60` some frames call it once, others
   twice.
2. **`xvel += inputDataX * YerdeHizlanma` then `MovePosition`** — per-step accumulation of a
   *positional* step. Step count = frame count or it isn't reproducible.
3. **`turn = Prefs.Sensivity * turnScale * turnInput * Time.deltaTime * DonmeHizi`** in `RotateView`
   is render-rate, while everything it feeds (the velocity rotation, `lookAccum`-derived gain) is
   step-rate.

`Time.captureFramerate = tickRate` collapses all three into "exactly one Update and exactly one
FixedUpdate per tick", which is the only condition under which `tape[N]` deterministically means
"what the sim ate at tick N". That is why the tool defaults to it:

```csharp
// TasTool.MarkGameStarted(), recording branch - add if you agree:
if (!cfg.enforceFixedRate) { statusLine = "refusing to record: input resolution is frame-rate dependent"; return; }
```

## 2. The capture point, confirmed by `lookAccum`

`lookAccum` is written in `Update`, spent *and cleared* in `FixedUpdate`. A snapshot taken at the tick
tail (a `FixedUpdate` callback, running before that batch's Update) would store `lookAccum == 0` and
throw away a frame of turn on every restore; a snapshot taken at `LateUpdate` stores the pending
value, which is exactly the state between "consumed" and "spent". This is why
`TasClock.OnTickCapture` fires from `LateUpdate` — the controller's own Update/FixedUpdate split is
the proof, and the same reasoning is why the recorder re-samples at that point instead of reusing the
head-committed value.

## 3. `MouseLook` owns nothing worth snapshotting - the real hazard is the cursor lock

`public MouseLook mouseLook = new MouseLook();` — a `[Serializable]` **class**, holding the pitch/yaw
accumulators that `LookRotation()` reads and writes. A transform+rigidbody snapshot does not reach it.
Restore the body and camera transforms but not those floats and the next `RotateView()` recomputes
rotation from stale internal state → "my savestate resets my aim", which is the signature bug of
Unity rollback tools. `TasUnityBridge.SaveLook/LoadLook` handle it by field name (
`m_XRotation`/`m_YRotation` and the usual variants, via reflection since they're private); if the
lookup warns, add two mirror fields — that's the version I'd ship.

## 4. What I now snapshot on the controller (and why each one is load-bearing)

| field | why it can't be reconstructed |
|---|---|
| `lookAccum` | per-frame→per-step handoff, cleared on spend |
| `lastBunnyFrame` | `if (lastBunnyFrame != inputData.inputLastFrame) AddRelativeForce(gain);` — the one-push-per-input-frame gate itself |
| `active180` | selects the `Don180MaxHiz` clamp, cleared by a 0.2 s coroutine |
| `xvel` | clamped strafe accumulator feeding `MovePosition` |
| `inputDataX`, `input_x`, `input_y`, `yy`, `tap_x`, `dynamic_x`, `uinput_x`, `uinput_y` | the *resolved* input, which is not fully `InputData` (see §5) |
| `mag`, `mag2`, `ag`, `ag2`, `ag3` | read in `RotateView` while still holding the previous step's values |
| `m_IsGrounded`, `m_PreviouslyGrounded`, `m_Jumping`, `m_Jump`, `m_YRotation` | the grounded branch and the landing transition that fires `GroundEvent` and `StickToGroundHelper()` |
| `m_bunnyCarpan`, `m_bunnyYokCarpan`, `m_bunnyCarpanDelta`, `_bmax`, `_lastjump`, `speed`, `sensMultiplier` | carry/gain state the next jump depends on |
| `total_speed`, `total_carpan`, `total_frame`, `total_frame_carpan`, `total_force`, `max_speed`, `max_carpan1/2`, `max_carpan`, `max_yfactor` | the stats `StartGame` zeroes → the game itself calls them run-scoped, and they are the published numbers |
| `velXZ`, `vel`, `velLocal`, `inputData.direction/inputLastFrame` | re-read before the next step, so they can't wait for the rigidbody restore |
| `LevelFizik.MaxHizYDisabled`, `GameState.SurfModeFizik`, `TapMode`, `SurfSliding`, `ControlTypeCache` | **statics**. They gate the `MaxHizY`/`SurfMaxHizDikey` clamps and the auto-walk in `GetInput()`, and no per-component snapshot can see them — the single sneakiest desync class you have |
| `Rigidbody.isSleeping`, `useGravity` | the near-still path calls `m_RigidBody.Sleep()`, and a sleeping body ignores `AddRelativeForce` until woken |

Dead-but-harmful: `movementSettings.ForwardSpeed/BackwardSpeed/StrafeSpeed/RunMultiplier/
CurrentTargetSpeed`, `Running => false` and `SlopeMultiplier()` have no effect, so don't hash them;
`movementSettings.JumpForce` and `advancedSettings.*` do (jump impulse, both sphere casts) and are
hashed.

## 5. Two input paths that `InputData` cannot express

`GetInput()` doesn't read only `InputData`:

```csharp
tap_x = ButonManager.GetTapX();                                   // static tap queue
dynamic_x = SplitTouchControl.i.dt3.x * 10f;  sy = ... dt3.y * 5f; // touch pad
input_x = uinput_x;                                               // never assigned in this class
... if (tap_x != 0f) { input_x = tap_x; GameState.TapMode = true; }
```
and in `Update`: `bool held = inputData.jump || Input.GetButton("Jump");` plus
`held = SplitTouchControl.i.jump`.

Consequences, in order of severity:

* **A recorded touch/Tap run is not reproducible from `InputData` alone.** Fixed by storing the
  resolved pair in the tape's aux channels (`auxC/auxD`) and, on replay, feeding it back at the
  resolution point: `if (UnityTAS.TasUnityBridge.TryResolvedInput(out v)) return v;` at the end of
  `GetInput()`. One line, and it makes Tap/Analog runs authorable.
* **`Input.GetButton("Jump")` is a live device read inside the sim.** During replay it lets a hand on
  spacebar modify the "replayed" run, and it's an input path the tape never sees. Zero-edit
  mitigation already implemented: the bridge folds `Input.GetKey(Space)` and `SplitTouchControl.i.jump`
  into the recorded Jump bit, so nothing is silently *lost*. It can't stop the live key from adding
  force during playback — for that, `TasPlayback` probes the device at every feed and counts an
  `INPUT NOT IN TAPE` tick, which is the honest behaviour: you get told instead of getting a wrong run.
  That probe is `TasPlayback.LiveButtons` (`Func<uint>`, defaults to `TasLiveInput.PeekButtons()`), and
  it only looks at `IntegrityMask` = Jump|Fire, i.e. the bits that apply force; holding W while watching
  a replay is legal and stays quiet. It is deliberately a **button-only, pure** reader and not the
  bridge's frame sampler: the frame sampler is fed the tape during replay (comparing it to the tape is
  vacuously true) and `MouseDelta()` would consume the mouse delta the capture in `LateUpdate` still
  needs, which would record zero look for a run that never had a look input problem.
* `xvel`/`inputDataX` need **no** patch, since both derive from `inputData.left/right` — the tape's
  DirLeft/DirRight bits. Same for `input_y` from `fwd/bck`. They are recorded as integrity checks only.
* `inputLastFrame` semantics: `lastBunnyFrame != inputLastFrame` only ever tests *inequality*, so any
  value that changes exactly once per tick is equivalent. `BuildInputData` stamps `Time.frameCount` to
  match what `UpdateControls` writes during recording. If you ever move to `TasClock.Tick`, do it in
  both paths, and note that with 2 physics steps in a frame the second gets no push (a real
  frame-rate dependence, §1).

## 6. `ag == 180f` — the branch that will eat your verification

```csharp
ag = Vector3.Angle(Vector3.forward * input.y, velXZ);
...
if (ag == 180f) { m_RigidBody.velocity = Vector3.zero; }
```
An exact `==` on a float from `Vector3.Angle`, guarding a **velocity zeroing**. Any 1e-7 difference —
different CPU's `acos`, different Unity patch, a collider moved 0.001 units — flips between "keep your
speed" and "dead stop", and from that tick the run is a different run. Practical implications:

* Same-machine `ResimLive`/`ResimVerify` is realistic. Cross-CPU x86↔ARM64 verification of *this game*
  will sometimes produce a legitimately different result, so a "cheat" verdict must never be automatic.
* The tool makes the frequency of this branch measurable (`fromStrafe/fromLook`/`ag` per tick are all
  in the tape). Decide once, with data: `Mathf.Approximately`-style epsilon, or `ag > 179.9f`.
* Related: `hitInfo.collider.gameObject.name.Contains("slide")` means **collider naming is physics**.
  `mapHash` should therefore be a hash of map *geometry* (collider count + trigger count + bounds
  quantised), not the map name — "same map, tweaked geometry" is otherwise an open leaderboard hole.

## 7. The six edits I'd make to `RigidbodyFirstPersonController`

```diff
-        public float m_bunnyCarpanDelta;
+        public float m_bunnyCarpanDelta;
+        // --- TAS tool accessors (see Docs/TasControllerAudit.md §7) ---
+        public float TasLookAccum { get { return lookAccum; } set { lookAccum = value; } }
+        public float TasYaw { get { return m_YRotation; } set { m_YRotation = value; } }
+        public Vector3 TasGroundNormal { get { return m_GroundContactNormal; } set { m_GroundContactNormal = value; } }
```

```diff
         private void Update()
         {
             FetchInput();
             RotateView();
             lookAccum += Mathf.Abs(inputData.look_x);
             m_Jump = false;
-            bool held = inputData.jump || Input.GetButton("Jump");
+            bool held = inputData.jump;      // keyboard jump already arrives via InputData; a raw
+                                             // GetButton here is an input path no tape can record
```

```diff
         private void RotateView()
         {
             if (Cursor.lockState != CursorLockMode.Locked) return;
-            if (Mathf.Abs(Time.timeScale) < float.Epsilon) return;
+            // With the tool driving playback the world may be time-frozen; the tape is the authority.
+            if (Mathf.Abs(Time.timeScale) < float.Epsilon && !Tas.TasInput.SourcedFromTape) return;
```

```diff
-                turn = Prefs.Sensivity * turnScale * turnInput * Time.deltaTime * DonmeHizi;
+                // TasClock.Delta == 1/tickRate always, so the turn no longer depends on render rate.
+                turn = Prefs.Sensivity * turnScale * turnInput * Tas.TasClock.Delta * DonmeHizi;
```

```diff
             yy = input_y;
+            // Tape override: lets a Tap/Analog run replay, where input_x/yy come from
+            // ButonManager/SplitTouchControl and NOT from InputData.
+            Vector2 tasResolved;
+            if (UnityTAS.TasUnityBridge.TryResolvedInput(out tasResolved)) return tasResolved;
             ControlTypes ct = GameState.ControlTypeCache;
```
(that goes immediately before the final `return new Vector2(input_x, yy);` — the last two lines of
`GetInput()`.)

```diff
         private IEnumerator don180()
         {
             active180 = true;
             base.transform.Rotate(Vector3.up, 180f);
-            yield return new WaitForSeconds(0.2f);
+            // WaitForSeconds runs on scaled time, which the tool's Hold-mode slow-mo cannot pause:
+            // at 1/4x the flag would clear 4 ticks too early in sim time and the Don180MaxHiz clamp
+            // would vanish mid-replay. This one line makes the wait tick-exact.
+            float t = 0f;
+            while (t < 0.2f) { if (!Tas.TasClock.CancelledFrame) t += Tas.TasClock.Delta; yield return null; }
             active180 = false;
         }
```

That's the whole list. Edits 1, 2, 4 and 6 are cheap and safe; 3 and 5 are the ones that decide
whether Tap-mode runs and >1x turbo work at all.

## 8. Sensitivity asymmetry, since a TAS leaderboard will surface it

```csharp
float sens = Prefs.Sensivity;
if (sens < 1f) { fromStrafe *= sens; fromLook *= sens; }
```
and in `RotateView`, `mouseLook.mouse_x = sensMultiplier * inputData.look_x * Prefs.Sensivity * 7f` —
with no `sens < 1` guard. So sensitivity scales the *turn* always, but the *bunny gain* only below 1.
Two things follow. First, `Prefs.Sensivity` is part of what a tick means, so it's now in
`configHash` (as is `Application.isMobilePlatform`, because `if (!Application.isMobilePlatform)
sensMultiplier = 1f` makes the **build platform** part of your physics). A tape at sens 0.9 replayed
at 2.4 is refused rather than silently faster — that mismatch would otherwise have looked like
verification working. Second, expect people to hunt the sensitivity that maximises gain. That's a
balance question, not a tool question, but publishing TAS-verified leaderboards turns it into a
player-facing one. Decide before you ship the toggle.

## 9. First test to run, once the six edits are in

1. `Prefs.Sensivity` 1.0, PC build, `enforceFixedRate` on. Record 30 s of pure bunnyhop, `F11`.
2. Fresh process, replay that tape `ResimLive`: expect `desync 0 corrections 0` and
   `INPUT NOT IN TAPE` count 0. Non-zero `INPUT NOT IN TAPE` = the Jump leak is still there.
3. `Shift+1` at a hard strafe, fail it, `1`, succeed it, save the run. Compare `max_speed` /
   `max_carpan` / `total_carpan` in the inspector against what the first attempt showed at that tick.
   A mismatch names the field still missing from the snapshot.
4. Slow to `1/4`, do the same segment, return to 1x, replay. If the run changed, the culprit is
   almost certainly `active180`/`GetTapX()` — i.e. edit 5 or 6 not applied.
5. Only then flip `autoReplayOnGameStart` on for real sessions.
