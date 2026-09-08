# ts

Native PC TAS tool + deterministic replay tapes for the game's leaderboard demos.

* [`Docs/TasToolPcGuide.md`](Docs/TasToolPcGuide.md) — **start here**: save states, slow-mo,
  auto-record on game start, the replay toggle, and how rollback branches are stitched into one
  continuous run. Quickstart is 3 steps and needs 2 lines wired into your existing code.
* [`Docs/InstallInUnityProject.md`](Docs/InstallInUnityProject.md) — **putting it in your project**:
  which folder each file goes in (the asmdef rule that trips everyone), the 3 edits to your code,
  nothing-to-add-to-your-scene, how to check the install took, and how to ship a build where the tool
  is compiled out instead of merely switched off.
* [`Docs/TasGameManagerIntegration.md`](Docs/TasGameManagerIntegration.md) — the wiring for *this*
  codebase: `StartGame` / `Olay_OyuncuDustu` / `OnLevelCompleted`, the `InputManager` seam, what must
  go in a savestate, and the rollback-vs-economy exploit you have to close first.
* [`Docs/TasReplayDesign.md`](Docs/TasReplayDesign.md) — how the current `DemoRecorder` works, why a
  TAS replay must record *inputs per tick* instead of transforms, determinism hazards, wire format.
* [`Tas/Runtime/`](Tas/Runtime) — clock, input seam, tape, recorder, playback, savestates, branches,
  leaderboard export, HUD. Drop-in replacement for `DemoRecorder`.
* [`Tas/Editor/TasToolWindow.cs`](Tas/Editor/TasToolWindow.cs) — `Window ▸ TAS Tool`.
* [`Docs/TasControllerAudit.md`](Docs/TasControllerAudit.md) — what your bunny-hop controller
  demands: the frame-rate-dependence finding, the full savestate field list, and the 6-line patch.
* [`Integration/`](Integration/) — the 3 game-side files (`TasGameBridge`, `TasUnityBridge`,
  `TasSuspend`) that live in Assembly-CSharp because they're the only things allowed to know about
  `GameManager`/`InputManager`/`Prefs`/`GameState`.

Two gates, deliberately different kinds:

* `TasGate.Available` is **compile-time** (`TAS_TOOL` define, or `UNITY_EDITOR`/`DEVELOPMENT_BUILD`).
  A release build without the define never boots `TasTool`, so there is no hotkey handler, no panel and
  no input-injection path for a client to be talked into using.
* `TasToolConfig.toolEnabled = false` is the runtime kill switch for a build that *does* have the tool.
