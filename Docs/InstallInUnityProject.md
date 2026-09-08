# Installing the TAS tool into your Unity project

This repo is a plain git repo, not your Unity project — so it's a copy (or a local UPM reference) and
then three small edits in your own files. Budget 30–45 minutes including the controller patch.

## 0. Requirements

| | |
|---|---|
| Unity | 2021.3+. 2022.2+/6000.x also fine — `TasPhysics` and `TasClock` `#if` over the `Physics.autoSimulation` → `Physics.simulationMode` and `Time.fixedDeltaTime` → `Physics.fixedDeltaTime` moves |
| Input | **old Input Manager** (you're on it: `Input.GetAxis`, `Input.GetButton("Jump")`, `Input.touchCount`). No new-Input-System dependency anywhere. `Tas.asmdef` deliberately references nothing |
| Api Compatibility Level | `.NET Standard 2.0` or `.NET 4.x` — `TasDemoExport` uses `System.IO.Compression.GZipStream`. On `.NET 3.5` delete `Gzip/Gunzip/ToWire/FromWire` (transport only, nothing else needs it) |
| Axes | default `Mouse X`, `Mouse Y`, `Jump` (your `InputManager` already relies on them) |
| Build target for the tool | PC. `TasLiveInput` is keyboard/mouse; touch runs are reproduced through the resolved aux channels |

## 1. Folder layout — this is the part people get wrong

```
Assets/
├── Tas/                       ← copy this whole folder from the repo
│   ├── Runtime/               TasClock, TasTool, tape, savestates…  + Tas.asmdef
│   ├── Editor/                TasToolWindow.cs                        + Tas.Editor.asmdef
│   └── package.json           (only used if you go the UPM route in §1b)
└── Scripts/                   ← your game code (Assembly-CSharp)
    ├── TasGameBridge.cs       ┐
    ├── TasUnityBridge.cs      ├─ the 3 files from the repo's Integration/ folder
    └── TasSuspend.cs          ┘
```

**Why the split is mandatory:** `Tas/` is a *script assembly* with no reference to your game, so it
cannot see `GameManager`/`InputData`/`Prefs`. `Tas.asmdef` sets `autoReferenced: true`, which means
`Assembly-CSharp` sees **it** — so the bridge files must live in `Assembly-CSharp`, i.e. in a folder
with **no `.asmdef`**. If you put them inside `Assets/Tas/`, you get
`error CS0246: The type or namespace name 'GameManager' could not be found`.

If your game code is *also* behind asmdefs (likely in a project this size), don't move it — add one
folder of your own:

```
Assets/Scripts/TasIntegration/TasIntegration.asmdef
{
    "name": "TasIntegration",
    "references": [ "Tas", "YourGameAssemblyName" ],   ← whatever assembly owns GameManager
    "includePlatforms": [],
    "autoReferenced": true
}
```
…and put the 3 bridge files there. Everything else in this guide is unchanged.

## 1b. Alternative: local UPM package (optional)

If you'd rather not litter `Assets/`, the package half can be a real dependency:

```jsonc
// Packages/manifest.json
"com.yourstudio.tas": "file:C:/Repos/ts/Tas"
```
Point it at the **`Tas` folder** (that's where `package.json` is), not the repo root. The `Editor/`
subfolder is picked up as an editor-only module automatically because of `Tas.Editor.asmdef`.
The 3 `Integration/` files still go in `Assets/` — they can't be in the package, since a package
must not depend on the game.

## 2. Three edits in your files

1. **`InputManager.GetInputData()`** — the two lines you already have. Keep them as written;
   `TasInputBus`/`TasUnityBridge` now exist with exactly those names:
   ```csharp
   if (TasInputBus.Active) return TasUnityBridge.BuildInputData(TasInputBus.Current);
   return controlEnabled ? input : emptyInput;
   ```
   While you're in the file: **delete `using UnityEditor;`** (unused, and a player-build break) plus
   the unused `using System;` / `using System.Collections.Generic;`.
2. **`RigidbodyFirstPersonController`** — the 6 edits in
   [`TasControllerAudit.md`](TasControllerAudit.md) §7. Three properties + the `GetInput()` override
   line are what make rollback/slow-mo/Tap-mode exact; the `Input.GetButton("Jump")` removal is what
   makes the tape actually complete.
3. **Optional, for frame-exact finish:** first line of `GameManager.Olay_LevelTamamlandi`:
   `Tas.TasTool.NotifyFinishExact("line");`

`Prefs`/`CachedPrefs`/`InventoryHelpers` writes: gate them on `TasGameBridge.PersistenceAllowed`
(one `if` per setter). Not optional if the tool ever reaches a build where the economy is live — see
§5 of the GameManager integration doc.

## 3. Nothing to add to a scene

`TasTool`, `TasGameBridge` and `TasSuspend` create themselves via
`[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]` + `DontDestroyOnLoad`.
`TasTool` spawns `TasClock`, `TasRecorder`, `TasPlayback`, `TasOverlay`, `TasToolUI` and
`TasClockTail` on its own GameObject, and `TasGameBridge` attaches `TasPlayerRunStats` to your player
and registers the tick driver.

Prefer explicit setup? Add an empty GameObject named `Tas` to your boot scene with `TasTool` on it and
delete the `[RuntimeInitializeOnLoadMethod]` method in `TasTool.AutoBoot` — otherwise you get two, and
the second is destroyed on the `if (i != null && i != this)` guard, which is fine but noisy in logs.

## 4. Verify the install took (2 minutes)

1. Compile clean, then **`Window ▸ TAS Tool`** exists.
2. Play. Console should show:
   * `[TAS] bridge bound | InputData is a struct (safe copies), screenInch=…`
   * `[TAS] StartNewRecording @tick … 60Hz` on the first run after `StartGame`
   If `screenInch=0.000` or "bridge bound" never appears, `InputManager.i` wasn't ready when the
   bridge polled — it re-binds every 0.05 s, so check for a null `m_playerController` on the player.
3. Filesystem: `Application.persistentDataPath` + `/tas/config.json` must exist. On Windows that's
   `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\tas\`. That folder is where `.tas`
   runs, `slot_N.tasst` savestates and `last_demo.json` land.
4. `F9` panel → `F11` stop → a `.tas` appears → `F12` (replay toggle) → restart the level → it plays
   back with `desync 0` and `INPUT NOT IN TAPE` absent. If you see `INPUT NOT IN TAPE`, §5 of the
   controller audit is the cause (a device read inside the sim).
5. Refused-to-record message `REFUSED to record: enforceFixedRate is off` means the guard is working —
   don't disable it until the controller patch has landed.

## 5. Building a PC player with the tool

Release builds must not contain the tool, and a runtime bool isn't enough (config.json is editable, so
a shipped `toolEnabled` flag is a cheat menu one text-file write away). `TasGate` makes it a compile
decision:

```csharp
#if TAS_TOOL || UNITY_EDITOR || DEVELOPMENT_BUILD
        public const bool Available = true;
```

* **Tool build (the one you TAS with):** Project Settings → Player → *Scripting Define Symbols* add
  `TAS_TOOL`; or just build a Development Build. Same binary as shipped, so what you TAS is what
  players run — which is the entire point.
* **Release build:** no define, not Development → `AutoBoot` returns at a `false` constant, the type
  is still in the assembly but nothing instantiates it, no hotkeys, no panel, no input injection, and
  `TasInputBus.Active` can never become true. If you want it *stripped* too, add
  `"defineConstraints": ["TAS_TOOL"]` to `Tas.asmdef` and move the 3 bridge files behind the same
  `#if` — then the player build doesn't even compile the recorder.
* **`link.xml`:** only needed if you use `TasTickDriver` (it invokes `Update` by reflection for >1x
  turbo) or Manual mode:
  ```xml
  <linker>
    <type fullname="UnityStandardAssets.Characters.FirstPerson.RigidbodyFirstPersonController" preserve="all"/>
  </linker>
  ```
  `MouseLook` needs nothing — the shipped `LookRotation` reads `localRotation` fresh, so there's no
  private accumulator to preserve and no reflection on it.
* `TasSuspend`'s deferred writes survive a scene reload (its GameObject is `DontDestroyOnLoad`), but
  **not** an app quit: if a TAS session can be killed mid-run, write the tape before flushing, which
  `StopRecording(true)` already does — don't move the flush earlier than the save.

## 6. Day-to-day use

`F9` panel · `F10` record/stop · `F11` stop all · `F12` replay toggle · `Backspace` rewind 1 s ·
`.`/`,` frame-step · `-`/`=` slow-mo · `[` 1x · `]` pause · digits save slots, `Shift+digit` load ·
`Ctrl+S` save the trunk. `Window ▸ TAS Tool` in the editor adds the tick grid, branch splicing,
`Verify tape` and `Export leaderboard JSON`. Details and mechanism in
[`TasToolPcGuide.md`](TasToolPcGuide.md).

Tapes live in `persistentDataPath/tas/`. To send one to yourself from a PC build, zip that folder —
`.tas` is ~86 KB per minute of uncompressed input, ~10 KB gzipped, and a slot savestate is ~1.5 KB.

## 7. Removing it

Delete the 3 `Integration` files from `Assets/Scripts/`, remove the `if (TasInputBus.Active)` lines
from `GetInputData()`, delete `Assets/Tas/`. Nothing in your game references the tool otherwise, by
design — that asymmetry (tool depends on nothing, game depends on 5 lines) is what keeps this from
becoming a permanent fork of your movement code.

## 8. Known not-done

* Not compiled — there is no Unity toolchain in this sandbox. Expect CS-level fixes on first import
  (the `⚠` comments in `Integration/` mark every spot where a member name came from inference rather
  than a file I could read).
* `TasSuspend` is a mechanism, not a policy: the call sites inside `Prefs` setters are yours.
* Headless server verification (`ResimVerify` on a Linux box) needs a Unity Player build on the
  verifier, since the physics is Unity's. The structural checks in `TasToolWindow.Verify` are the
  no-Unity fallback.
* `TasPlayerRunStats` reads `dbg_sens`/`_lastjump`/`xvel`/… as public fields because your controller
  has them public. If any get made private, the compiler will tell you — that's intentional: a field
  that becomes unreadable should fail loudly, not silently skip out of the snapshot.
