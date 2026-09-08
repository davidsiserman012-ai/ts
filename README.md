# ts

Native PC TAS tool + deterministic replay tapes for the game's leaderboard demos.

* [`Docs/TasToolPcGuide.md`](Docs/TasToolPcGuide.md) — **start here**: save states, slow-mo,
  auto-record on game start, the replay toggle, and how rollback branches are stitched into one
  continuous run. Quickstart is 3 steps and needs 2 lines wired into your existing code.
* [`Docs/TasReplayDesign.md`](Docs/TasReplayDesign.md) — how the current `DemoRecorder` works, why a
  TAS replay must record *inputs per tick* instead of transforms, determinism hazards, wire format.
* [`Tas/Runtime/`](Tas/Runtime) — clock, input seam, tape, recorder, playback, savestates, branches,
  leaderboard export, HUD. Drop-in replacement for `DemoRecorder`.
* [`Tas/Editor/TasToolWindow.cs`](Tas/Editor/TasToolWindow.cs) — `Window ▸ TAS Tool`.

Sentinel: `TasToolConfig.toolEnabled = false` makes the whole tool inert and invisible in a
release build — no hotkeys, no panel, no capture, and no code path that a shipped client can be
talked into driving.
