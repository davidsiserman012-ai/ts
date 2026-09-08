# ts

Native TAS tool + deterministic replay tape for the game's leaderboard demos.

* [`Docs/TasReplayDesign.md`](Docs/TasReplayDesign.md) — how the existing `DemoRecorder`
  pipeline works, why a TAS replay must record *inputs per tick* instead of transforms, and
  the migration plan.
* [`Tas/Runtime/`](Tas/Runtime) — clock, input seam, tape format, recorder, playback,
  savestates, leaderboard export, HUD. Drop-in replacement for `DemoRecorder`.
* [`Tas/Editor/TasToolWindow.cs`](Tas/Editor/TasToolWindow.cs) — `Window ▸ TAS Tool`.

Start with phase 0 of the design doc: land `TasClock` + `TasInput`, keep `DemoRecorder`
running beside it, and diff the two for a week before you switch the leaderboard over.
