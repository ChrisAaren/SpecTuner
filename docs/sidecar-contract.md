# Sidecar contract

The sidecar (`spectuner-sidecar.exe`) does all Unity-specific work. The
TypeScript tools and the desktop UI only pass requests through and show
results. This file is the interface both sides build against.

## Invocation

```
spectuner-sidecar.exe <command>          # JSON request on stdin
spectuner-sidecar.exe <command> --request <file.json>
```

- One JSON object in, one JSON object out on stdout. Nothing else is written
  to stdout. Diagnostics go to stderr.
- Exit code 0 whenever a JSON envelope was written, including refusals and
  handled failures. On an unexpected internal error (a bug) the sidecar still
  writes an envelope with code `INTERNAL_ERROR` (technical detail in
  `error.detail`, stack trace on stderr) and exits 1.
- Strings are UTF-8 on stdin and stdout.
- Property names are camelCase.
- `SPECTUNER_HOME` overrides the state root (default
  `%LOCALAPPDATA%\SpecTuner`). Tests always set it.

### Envelope

```jsonc
// success
{ "ok": true, "command": "detect", "summary": "One plain-language sentence.", "data": { } }
// refusal or handled failure
{ "ok": false, "command": "detect",
  "error": { "code": "IL2CPP_NOT_SUPPORTED", "message": "Plain-language explanation for the player.", "detail": "optional technical detail" },
  "data": { } }
```

`summary` and `error.message` are written for a non-technical player: no file
paths unless needed, no stack traces, no code.

Output must stay small. Lists are capped, and a `truncated` flag says so.

### Error codes outside the scope gate

| Code | When |
|---|---|
| `UNKNOWN_COMMAND` | No command, or a command name the sidecar does not know |
| `INVALID_JSON` | The request is not valid JSON |
| `BAD_REQUEST` | The request is not an object, a field is missing or has the wrong type, or bad command-line options |
| `INTERNAL_ERROR` | A bug (exit code 1) |
| `BUSY` | Another sidecar process is changing the same game right now |
| `GAME_RUNNING` | A change or revert was asked for while the game is running |
| `LOADER_CONFLICT` | The folder already has another mod loader or leftover loader files (`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, a partial `BepInEx/`, `MelonLoader/`) |
| `UNSUPPORTED_LOADER` | The player's own BepInEx is not 5.x |
| `CHANGE_NOT_FOUND` | `revert` or `reapply` with an unknown `changeId` |
| `CHANGE_NOT_ACTIVE` | `revert` of a change that was already undone |
| `REVERT_BLOCKED` | `revert` of one change that others depend on (see `revert`) |
| `CHANGE_STILL_ACTIVE` | `reapply` of a change that is in place (or whose reapplied copy is) |
| `CANNOT_REAPPLY` | `reapply` of a loader/probe change or of a past change with `canReapply: false`; the message says why |
| `PROBE_BUILD_FAILED` | The probe plugin did not compile against this game's assemblies; nothing was changed. `data.errors` (at most 15) and `data.errorCount` |
| `INVALID_SETTING` | `set-quality` with an unknown key or a value out of range; the message says what is valid |
| `GAME_FAILED_TO_START` | `launch-and-benchmark`, or `build-plugin`'s test launch, got no result (see `launch-and-benchmark`) |
| `MOD_FAILED_TEST` | `build-plugin`'s test launch: the game started but the new mod did not load or logged errors, so it was removed again (see `build-plugin`) |
| `LOG_NOT_FOUND` | `read-logs` and the log does not exist yet |
| `ASSEMBLY_NOT_FOUND` | `search-code`/`read-code` with an `assembly` the game does not have (the message lists the game's own ones), or no game code at all |
| `TYPE_NOT_FOUND` | `read-code` with an unknown type; `data.nearest` lists close names |
| `AMBIGUOUS_TYPE` | `read-code` with a simple name that several types share; `data.candidates` lists them |
| `MEMBER_NOT_FOUND` | `read-code` with an unknown member; `data.nearest` lists close names |
| `COMPILE_FAILED` | `build-plugin` source does not compile; `data.errors` (at most 15, each with `id`, `file`, `line`, `column`, `message`, and a `hint` for common mistakes) and `data.errorCount` |
| `INVALID_PLUGIN` | `build-plugin` source compiles but is not exactly one `[BepInPlugin]` class deriving from `BaseUnityPlugin`, or its GUID is taken |
| `UNSAFE_CODE` | `build-plugin` source uses something on the deny list; `data.refused` lists each construct with its reason and line |

## State

Per game, under `<SPECTUNER_HOME>/games/<id>/` where `<id>` is the first 16
hex chars of SHA-256 (UTF-8) of the lower-cased full game path, normalized
with `Path.GetFullPath` and without a trailing separator
(e.g. `c:\games\sample game`). All JSON state is written atomically
(temp file, then move). A `lock` file is held while a command changes the game.

| File | Purpose |
|---|---|
| `game.json` | Game path, name, first-seen time, `loader` (`{ "ours": bool, "version": "5.4.23.6" }` or null), `probe` (`{ "sourceHash", "builtAt" }` of the installed probe, or null), and `restores` (one record per full restore) |
| `baseline.json` | Manifest of every file in the game folder as first found: relative path (forward slashes), size, last-write time (UTC), SHA-256, plus the list of folders. Written once, before anything is added. |
| `changes.json` | Ordered ledger of changes (see below) |
| `history.json` | `{ "changes": [ …ledger entries… ] }`: the entries of earlier rounds, moved here by each full restore (oldest first, at most 1000). Kept so past changes can be shown and applied again (`reapply`), and so ids are never reused |
| `snapshots/<changeId>/` | Copy of our own mutable files (`BepInEx/plugins/SpecTuner/**`, `BepInEx/config/spectuner.*.json`) as they were before that change: `snapshot.json` (list) plus `files/` |
| `mods/<name>/` | `plugin.json` (GUID, plugin name, version, assembly name, type names, change id of the build in place) and `<changeId>/<file>.cs`, the source of every build, used by `reapply`. Kept after a full restore |
| `decompiled/<assemblyHash>/` | Decompiled C# cache: `types/<Type>.cs` (one file per top-level type; nested types are inside their parent's file) and `index.json` (every type and member with kind, signature and line range). `<assemblyHash>` is the first 16 hex chars of the dll's SHA-256, so a game update rebuilds it. Kept after a full restore. |
| `benchmarks.json` | `{ "runs": [ { runId, label, at, ok, avgFps, low1PctFps, p95Ms, frames, resolution, failure } ] }`, every run since the last full restore |

Inside the game folder the sidecar writes only:

- the BepInEx loader files: `BepInEx/`, `winhttp.dll`, `doorstop_config.ini`,
  `.doorstop_version` (the zip's `changelog.txt` is never extracted)
- `BepInEx/plugins/SpecTuner/` (probe plugin and generated mods)
- `BepInEx/config/spectuner.*.json` (quality overrides, benchmark request)
- `BepInEx/spectuner/` (probe output: `hardware.json`, `settings.json`,
  `benchmark-<runId>.json`)
- `BepInEx/config/BepInEx.cfg`, only in a BepInEx we installed, and only for
  Unity 6000+: it sets `[Preloader.Entrypoint] Type = Camera`. BepInEx's
  default (`Application`) starts plugins too early on Unity 6000.6 and the game
  crashes in `GameObject.AddComponent` as soon as any plugin is loaded. This
  file is part of the `loader` change.

`BepInEx/config/spectuner.benchmark.json` is a one-shot request: the probe
deletes it when it reads it, the sidecar deletes it after every run, it is
never snapshotted and never part of a change.

Original game files are never edited, replaced, renamed, or deleted.

After a full restore the per-game state is cleared (baseline, ledger,
snapshots, benchmark history); `game.json` stays, with the restore recorded in
`restores`, and so do `history.json` (the ledger's entries are appended to it)
and `mods/`. The next `install` (or any change) takes a fresh baseline.

### Change ledger entry

```jsonc
{
  "id": "c0003",                 // stable id
  "kind": "loader" | "probe" | "quality" | "mod",
  "title": "Lower shadow distance",        // short, plain language
  "description": "…",                      // one or two plain sentences
  "createdAt": "2026-11-21T04:10:00Z",
  "status": "pending" | "active" | "reverted",  // pending: begun, not committed (sidecar stopped halfway); treated as live
  "verified": true,              // false until the game has started successfully with it
  "files": ["BepInEx/plugins/SpecTuner/DoubleJump.dll"],
  "revertedAt": "2026-11-21T05:00:00Z",  // only once reverted
  "endReason": "undone",         // only once reverted: "undone" | "restored" | "failed_start" | "failed_test" | "replaced"
  "settings": { "shadowDistance": 40 },    // quality changes only: the "settings" object set-quality applied (recorded from this version on)
  "reappliedFrom": "c0002"       // only for a change made by reapply
}
```

Ids are `c` plus a 4-digit counter, unique within the ledger and
`history.json` (they continue after a full restore).

`endReason`: `undone` by `revert` with a `changeId`; `restored` by `revert`
`all`; `failed_start` when a launch (`launch-and-benchmark` or `build-plugin`'s
test) failed and it was reverted automatically (a full restore caused by a
failed first start uses it too); `failed_test` when `build-plugin`'s test
launch removed the mod; `replaced` for a mod build that a newer build of the
same mod had already replaced when "Restore game" ran (its `revertedAt` is then
the newer build's `createdAt`). A replaced build that is still in the ledger
stays `active` (undoing the newer build brings it back); `changes` reports it
as past with reason `replaced`.

## Scope gate

Every command except `detect` runs the same check first and refuses with the
same error when the game is out of scope. Codes:

| Code | When |
|---|---|
| `GAME_NOT_FOUND` | Folder missing or no game exe in it |
| `NOT_UNITY` | No `UnityPlayer.dll` / `<Name>_Data` pair |
| `IL2CPP_NOT_SUPPORTED` | `GameAssembly.dll` or `<Name>_Data/il2cpp_data` present |
| `NOT_WINDOWS_X64` | Game exe is not a 64-bit Windows executable |
| `ANTI_CHEAT` | Easy Anti-Cheat, BattlEye, or similar files present |
| `PROTECTED_CODE` | Game code is encrypted, packed, or DRM-wrapped |
| `ONLINE_MULTIPLAYER` | Networking libraries for online multiplayer present |

The sidecar only detects these. It never bypasses, disables, or works around
any of them.

Exception: `changes` and `revert` still run for a game that is now out of
scope (for example a game update added a networking library) when SpecTuner
already has state for it, so the player can always see and undo what
SpecTuner did. `GAME_NOT_FOUND` is never bypassed.

Checks run in the order of the table. Details:
- Game exe: the `*.exe` in the folder root (not `UnityCrashHandler*`) with a
  sibling `<Name>_Data` folder. Unity also needs `UnityPlayer.dll`, or for old
  Unity versions `globalgamemanagers` / `mainData` / `data.unity3d` in the data
  folder. A non-IL2CPP game without `<Name>_Data/Managed` is `NOT_UNITY`.
- Anti-cheat: file and folder name patterns anywhere in the game folder.
- Protected code: the exe has a `.bind` PE section (Steam DRM), or
  `Managed/Assembly-CSharp.dll` exists but has no valid .NET metadata.
- Online multiplayer: networking assembly names in `<Name>_Data/Managed/`.
  Unity sign-in, cloud save, analytics (`Unity.Services.Authentication*`,
  `Unity.Services.CloudSave`, `Unity.Services.Core*`) and Steamworks.NET do
  not count.
- The pattern tables live in `sidecar/src/SpecTuner.Sidecar/Games/Signatures.cs`.

## Commands

### `detect`
Request: `{ "gamePath": "C:/Games/Sample Game" }`

Makes no changes. Data:

```jsonc
{
  "supported": true,
  "game": { "name": "Sample Game", "exe": "Sample Game.exe", "unityVersion": "6000.6.9f1",
            "backend": "mono", "arch": "x64", "company": "DefaultCompany" },
  "loader": { "installed": false, "ours": false, "version": null },
  "probe": { "installed": false },   // true when a live, committed probe change exists
  "activeChanges": 0
}
```

When out of scope: `ok: false`, the error above, and `data.supported: false`
with whatever game facts were found.

### `install`
Request: `{ "gamePath": "…" }`

Writes the baseline manifest if there is none, installs BepInEx 5 (skipped if
the game already has BepInEx; then it is not ours and is never removed),
builds and installs the probe plugin. Records `loader` and `probe` changes.
Idempotent. Other commands that need the loader call this themselves.

- Refused with `GAME_RUNNING` while the game runs.
- Never overwrites a baseline file. If BepInEx 5 (`BepInEx/core/BepInEx.dll`)
  was there at baseline, it is the player's: no files are added for the
  loader and no `loader` change is recorded (only `game.json` `loader.ours =
  false`). BepInEx that is not 5.x gives `UNSUPPORTED_LOADER`; leftover loader
  files without BepInEx give `LOADER_CONFLICT`.
- Run again, it puts back any of our loader files that went missing.
- Order: baseline, loader check (`UNSUPPORTED_LOADER` / `LOADER_CONFLICT`),
  probe compile (`PROBE_BUILD_FAILED`), and only then any write to the game
  folder. A refusal at any of these steps leaves the game folder untouched.
- The probe is compiled in-process (Roslyn, C# 9) from source embedded in the
  sidecar, against the game's `Managed/*.dll` and BepInEx's core dlls (from
  the game folder, or from the embedded package before BepInEx is extracted).
  It is rebuilt only when the embedded source changes (`game.json`
  `probe.sourceHash`); a rebuild is a new `probe` change.

Data:
```jsonc
{
  "baseline": { "created": true, "files": 214, "totalBytes": 395322768 },
  "loader": { "installed": true, "ours": true, "version": "5.4.23.6",
              "alreadyInstalled": false, "changeId": "c0001", "filesAdded": 22 },
  "probe": { "installed": true, "changeId": "c0002", "alreadyInstalled": false }
}
```

### `changes`
Request: `{ "gamePath": "…" }`

Data: `{ "changes": [ …ledger entries, newest first, reverted ones included… ], "total": 3, "truncated": false, "canRestore": true,
"mods": { "c0005": { "name": "DoubleJump", "build": 2 } },
"past": [ … ], "pastTotal": 2, "pastTruncated": false }`.
`mods` has one entry per mod change in `changes`, keyed by change id: the
mod's name and which build of that mod it is, counted the same way as
`past[].mod`, so the UI can tell two builds of one mod apart.
`canRestore` is true when any change is live. At most 100 entries.
Used by the UI for the list of changes and their Undo buttons. `changes`
covers the current ledger only (empty after a full restore).

`past` ("Past changes"): `quality` and `mod` changes that were once in place
and are not any more, from the ledger and from `history.json`, newest first
(by id), at most 50 (`pastTotal` is the full count). Loader and probe changes
are never listed: they are set up automatically. Each item:

```jsonc
{ "id": "c0004", "kind": "quality" | "mod", "title": "…", "description": "…",
  "createdAt": "…", "endedAt": "…",
  "reason": "undone" | "restored" | "failed_start" | "failed_test" | "replaced",
  "canReapply": true,
  "whyNot": "…",                                 // only when canReapply is false: one plain sentence
  "mod": { "name": "DoubleJump", "build": 2 },   // mods only: build = the nth build of that mod name
  "reappliedFrom": "c0002" }                     // only when this change was itself made by reapply
```

- An entry from before `endReason` was recorded counts as `undone`.
- `canReapply` is false for `failed_start` and `failed_test` (the change never
  worked), for a quality change without recorded `settings` (made by an older
  sidecar), and for a mod whose saved source is missing.
- Several builds of one mod are separate items (own id, times and `build`
  number).
- A past change that was applied again is left out while its newer copy is in
  place, or while that copy is itself in `past` and can be reapplied: the newest
  copy stands for it, so the same change is never offered twice. (`reapply` of
  the hidden original while its copy is in place gives `CHANGE_STILL_ACTIVE`.)

### `reapply`
Request: `{ "gamePath": "…", "changeId": "c0004", "test": true }`

"Apply again" for one item of `changes`' `past` list. Refused with
`GAME_RUNNING` while the game runs. Puts the change back as a NEW change (new
id) with the same title and description, marked `reappliedFrom`. Like every
change, it installs the loader and helper first if they are missing, so it works
after "Restore game".

- `quality`: the recorded `settings` go through `set-quality`'s own code
  (same validation, merged into the current overrides). Undoing the new change
  is the same as undoing a fresh `set-quality`. If those settings are already in
  place, nothing changes and `changeId` is null (as in `set-quality`).
- `mod`: rebuilt from `mods/<name>/<changeId>/` through `build-plugin`'s own
  code: safety check, compile against the game as it is now, install, and (with
  `test`, default true) the test launch, with removal on failure. If a build of
  the same mod is in place, the new one replaces it as a rebuild does
  (`replaces: true`, and the summary says it replaces the previous version).

Result: the underlying command's result with `"reappliedFrom": "<changeId>"`
added to `data`, and the summary prefixed with `Put back "<title>".`. So for a
quality change `{ changeId, changed, overrides, reappliedFrom }`, for a mod
`{ changeId, file, replaces, plugin, lookFor, strippedCodeFence: [], test, reappliedFrom }`.
Failures returned by the underlying command (`MOD_FAILED_TEST`,
`GAME_FAILED_TO_START`) also carry `reappliedFrom` in `data`; refusals it throws
(`COMPILE_FAILED`, `UNSAFE_CODE`, `INVALID_PLUGIN`, `INVALID_SETTING`, ...) are
passed through unchanged.

Errors of its own: `CHANGE_NOT_FOUND` (no change with that id, in the ledger or
history), `CHANGE_STILL_ACTIVE` (the change, or a copy of it made by
`reapply`, is in place), `CANNOT_REAPPLY` (a loader or probe change, or a past
change with `canReapply: false`; the message is its `whyNot`).

### `revert`
Request: `{ "gamePath": "…", "changeId": "c0003" }` or `{ "gamePath": "…", "all": true }`

Refused with `GAME_RUNNING` while the game runs. Exactly one of `changeId`
and `all` must be given (`BAD_REQUEST` otherwise).

- One change: puts our files back as they were before it and marks it
  reverted. `loader` and `probe` cannot be reverted alone while other changes
  are active; use `all` (`REVERT_BLOCKED`). Reverting `loader` when it is the
  only live change is the same as `all`. A change is also `REVERT_BLOCKED`
  while a later live change touched one of the same files; the message names
  the later change to undo first. Data: `{ "reverted": <entry>, "fullRestore": false }`.
- `all` ("Restore game"): removes everything we added, then checks the folder
  against the baseline manifest. Data reports `removed` (count),
  `matchesBaseline` (bool), `leftInPlace` (files that were not there at
  baseline and are not ours, e.g. written by the game itself; never deleted),
  and `changedOriginals` (baseline files whose content changed; never by us).
  Also: `missingOriginals` (baseline files that are gone; never by us), a
  `...Count` for each of the three lists (lists are capped at 50, `truncated`),
  `revertedChanges` (ids) and `fullRestore: true`. With nothing recorded for
  the game: `{ "removed": 0, "nothingToRestore": true }`.
- "Everything we added" means: when the loader is ours, all of `BepInEx/`
  (including what BepInEx wrote at runtime, such as `LogOutput.log`, `cache/`,
  `config/BepInEx.cfg`) and the three root loader files; always
  `BepInEx/plugins/SpecTuner/`, `BepInEx/spectuner/`,
  `BepInEx/config/spectuner.*.json`, and any file listed by a live change.
  Baseline files are never removed.
- Verification treats a baseline file whose size and last-write time are
  unchanged as unchanged; any other baseline file is re-hashed.

### `set-quality`
Request:
```jsonc
{ "gamePath": "…", "title": "Lower shadow distance", "description": "…",
  "settings": { "shadowDistance": 40, "antiAliasing": 0, "renderScale": 0.85 } }
```
Merges `settings` into `BepInEx/config/spectuner.quality.json` (`null`
removes a key; the file is deleted when no key is left). Takes a snapshot and
records one `quality` change. The probe applies the file at the next launch.
Installs the loader and probe first if needed. Refused with `GAME_RUNNING`
while the game runs. If the merged result equals the current file, nothing is
recorded and `changeId` is null. `title` defaults to "Change graphics
settings"; `description` defaults to a list of the changed keys.

Data: `{ "changeId": "c0003", "changed": ["renderScale"], "overrides": { …the whole file now… } }`.

Supported keys (unknown keys are refused with the list of valid ones):
`qualityLevel`, `vSyncCount`, `targetFrameRate`, `resolutionWidth`,
`resolutionHeight`, `fullscreenMode`, `renderScale`, `shadows`,
`shadowDistance`, `shadowResolution`, `shadowCascades`, `antiAliasing`,
`anisotropicFiltering`, `textureMipmapLimit`, `lodBias`, `maximumLODLevel`,
`pixelLightCount`, `softParticles`, `realtimeReflectionProbes`.

| Key | Valid values |
|---|---|
| `qualityLevel` | whole number 0..31, or 0..(levels-1) once the probe has reported the game's quality levels |
| `vSyncCount` | 0..4 |
| `targetFrameRate` | -1, or 10..1000 |
| `resolutionWidth`, `resolutionHeight` | 320..16384 and 200..16384; only together |
| `fullscreenMode` | `ExclusiveFullScreen`, `FullScreenWindow`, `MaximizedWindow`, `Windowed` |
| `renderScale` | 0.1..2.0 (URP only; reported as not in effect elsewhere) |
| `shadows` | `Disable`, `HardOnly`, `All` |
| `shadowDistance` | 0..10000 |
| `shadowResolution` | 256, 512, 1024, 2048, 4096, 8192 (pixels) |
| `shadowCascades` | 1..4 |
| `antiAliasing` | 0, 2, 4, 8 (MSAA samples) |
| `anisotropicFiltering` | `Disable`, `Enable`, `ForceEnable` |
| `textureMipmapLimit` | 0..3 |
| `lodBias` | 0.01..10 |
| `maximumLODLevel` | 0..7 |
| `pixelLightCount` | 0..64 |
| `softParticles`, `realtimeReflectionProbes` | true / false |

Choice values are matched case-insensitively and stored in the spelling above.

How the probe applies the file: at its start (one frame after the plugin's
`Start`), 3 s later, on every scene load, the frame after it and 3 s after it,
and at the start of a benchmark. After start-up it checks every second and
sets back any key the game changed (logged the first 3 times per key, counted
in the benchmark's `overrides.reapplied`). It applies built-in
`QualitySettings` and, through reflection, the active URP asset's matching
properties, in memory only. Each key that had to change is logged as one line
`SpecTuner: <pass>: <key> <old> -> <new>` in the BepInEx log.

### `launch-and-benchmark`
Request: `{ "gamePath": "…", "label": "baseline", "warmupSeconds": 10, "measureSeconds": 20, "startupTimeoutSeconds": 90 }`
(ranges 0..120, 1..300 and 5..600; all optional, `label` defaults to "run").

Same-launch A/B trial: add `"trySettings": { "shadowDistance": 15 }` and optionally
`"pairs": 4` (2..10), `"windowSeconds": 5` (2..60), `"settleSeconds": 2` (0.5..30).
`trySettings` uses the same keys and ranges as `set-quality`, except that `null` is
not allowed and these keys are refused with `INVALID_SETTING` (the message says to
use `set-quality` plus a normal benchmark instead): `qualityLevel` (it replaces the
game's whole set of graphics settings), `resolutionWidth`/`resolutionHeight` and
`fullscreenMode` (resizing the window takes a moment and disturbs the measurement),
`vSyncCount`/`targetFrameRate` (the cap is lifted while measuring). Bad trial
requests are refused before anything is installed or started. `measureSeconds` is
not used in a trial.

After warm-up the probe measures alternating windows A, B, A, B… (`pairs` of each).
A = the saved overrides, plus the game's own value (read just before the first
switch) for every tried key that has no saved override, so switching back really
undoes B. B = the saved overrides plus `trySettings`. After every switch it waits
`settleSeconds` without measuring. At the end it switches back to A. Nothing is
persisted: the overrides file is not touched, no change is recorded, and the tried
settings travel only in the one-shot benchmark request.

Starts the game, lets the probe measure frame times, closes the game, and
returns numbers. Frame cap and vsync are lifted during measurement in every
run so runs can be compared.

- Installs the loader and probe first if needed. Refused with `GAME_RUNNING`
  when the game is already running.
- Writes the benchmark request, starts `<Name>.exe` from the game folder with
  no arguments and its stdio redirected (so nothing reaches our stdout), inside
  a Windows job object with kill-on-close: if the sidecar is killed, the game
  dies too. It waits for `BepInEx/spectuner/benchmark-<runId>.json`, the game
  exiting, or `startupTimeoutSeconds + warmupSeconds + measureSeconds`. After
  the result the game gets 8 s to quit by itself, then it is closed.
- The probe waits until its start-up passes are done and a scene is loaded,
  then warms up and measures every frame's unscaled delta time. During warm-up
  and measurement it sets `Application.runInBackground = true`, `vSyncCount =
  0` and `targetFrameRate = -1`, re-asserting them every frame; these are never
  written to the overrides file. `vSyncCount`/`targetFrameRate` overrides are
  therefore not applied during a benchmark (`suspendedForMeasurement`).
- `low1PctFps` = 1000 / mean of the slowest max(1, n/100) frame times in ms.
  Percentiles are nearest-rank.
- `windowSpreadPct` = (max − min) / mean of the average FPS of sub-windows, in
  percent: the measurement split into 4 equal-time parts for an ordinary run,
  the A windows for a trial. It shows how steady the run was.
- Differences between separate launches under 10% in average FPS are within
  normal run-to-run variation: `withinNoise: true` in `comparedToPrevious` /
  `comparedToBaseline`, and the summary sentence says so instead of presenting
  a change.
- `runId` is `b` plus a 4-digit counter. `comparedToPrevious` uses the last
  successful run, `comparedToBaseline` the first successful run since the last
  full restore; both are null when there is none.

Data:
```jsonc
{
  "label": "baseline", "runId": "b0002",
  "avgFps": 142.3, "low1PctFps": 88.0,
  "frameMs": { "avg": 7.03, "p50": 6.8, "p95": 9.9, "p99": 11.4, "max": 31.0 },
  "frames": 2846, "seconds": 20.0,
  "resolution": "1920x1080", "scene": "Main",
  "settings": { "qualityLevel": 0, "qualityLevelName": "Low", "vSyncCount": 0, "targetFrameRate": -1,
                "resolution": "…", "fullscreenMode": "…", "renderPipeline": "UniversalRenderPipelineAsset (…)",
                "renderScale": 0.67, "shadows": "All", "shadowDistance": 40, "shadowResolution": 1024,
                "shadowCascades": 2, "antiAliasing": 0, "anisotropicFiltering": "…", "textureMipmapLimit": 1, "lodBias": 1 },
  "hardware": { "gpu": "…", "gpuMemoryMb": 8192, "graphicsApi": "Direct3D12", "cpu": "…", "cores": 8,
                "ramMb": 16384, "os": "…", "screen": "1920x1080", "refreshRateHz": 60 },
  "overrides": { "requested": { … }, "inEffect": { "renderScale": true }, "notInEffect": [],
                 "reapplied": { "renderScale": 3 }, "suspendedForMeasurement": [] },
  "frameCap": { "gameVSyncCount": 0, "gameTargetFrameRate": 120, "reasserted": 1 },
  "loadedPlugins": ["SpecTuner Probe 1.0.0", "Double Jump 1.0.0"],
  "mods": [ { "name": "DoubleJump", "plugin": "Double Jump 1.0.0", "loaded": true, "errorCount": 0 } ],
  "pluginErrors": { "Double Jump": ["[Error  : Unity Log] HarmonyException: … | DoubleJump.Plugin.Awake () …"] },
  "logErrors": [],                // at most 10, from this run's BepInEx log (which also carries Unity's log)
  "comparedToPrevious": { "runId": "b0001", "label": "…", "avgFpsDelta": 12.1, "avgFpsDeltaPct": 9.3,
                          "low1PctFpsDelta": 4.0, "low1PctFpsDeltaPct": 4.8, "p95MsDelta": -0.8 },
  "comparedToBaseline": { … same shape … },   // both also have "withinNoise": true | false
  "windowSpreadPct": 6.2,
  "trial": null,                              // or the block below
  "durationSeconds": 38.6
}
```

For a trial, every top-level number (and what goes into the history) comes from
the A windows only. History entries carry `trial: true|false`, and
`comparedToPrevious`/`comparedToBaseline` only compare runs of the same kind:
on the test game the frame rate follows the title scene's own timeline, so A
windows spread over a trial and one continuous 20 s window cover different
moments and differed by ~9% with nothing changed. `trial`:

```jsonc
{
  "settings": { "shadowDistance": 15 }, "pairs": 4, "windowSeconds": 5, "settleSeconds": 2,
  "a": { "avgFps": 470.1, "low1PctFps": 176.1, "p50Ms": 2.09, "p95Ms": 2.59, "frames": 9404 },
  "b": { … same shape … },
  "pairResults": [ { "aFps": 435.6, "bFps": 472.9, "deltaPct": 8.57 }, … ],   // deltaPct = (B − A) / A
  "meanDeltaPct": -1.92,
  "confirmed": ["shadowDistance"],      // tried keys that had the tried value at the end of every B window
  "notConfirmed": [],
  "verdict": "better" | "worse" | "no_clear_difference" | "not_applied",
  "reason": "One plain sentence."
}
```

Verdict rule (`sidecar/src/SpecTuner.Sidecar/Benchmark/Verdict.cs`):
`not_applied` when no tried key was confirmed; `better` when the mean paired
delta is at least +3% and every pair is positive, allowing one exception when
there are more than 3 pairs; `worse` is the mirror image; anything else is
`no_clear_difference`.

`settings` is read at the end of the measurement. `overrides.inEffect` says,
per requested key, whether the game actually had that value at the end, so a
setting that did not stick is never claimed. `frameCap` shows what the game
itself had set before the cap was lifted, and how often it had to be lifted
again.

If the game does not start, crashes, or produces no result: every change made
since the last successful start is reverted automatically, and the result is
`ok: false` with code `GAME_FAILED_TO_START` and `data.reverted` listing what
was undone. A successful start marks pending changes `verified`.

Failure data: `{ "runId", "reason": "crashed" | "exited" | "timeout" | "start-failed", "exitCode",
"reverted": [ { "id", "kind", "title" } ], "fullRestore", "logErrors", "durationSeconds" }`.
Undone newest first through the ledger; if the loader itself was never
verified, that is a full restore (`fullRestore: true`). Closing the game window
during a benchmark also counts as a failure.

### `read-logs`
Request: `{ "gamePath": "…", "source": "bepinex" | "player", "filter": "DoubleJump", "tail": 60 }`

Returns matching lines from the last run, newest last, capped. Errors and
exceptions are always included.

`bepinex` is `BepInEx/LogOutput.log`; `player` is
`%USERPROFILE%\AppData\LocalLow\<company>\<product>\Player.log` (from
`app.info`). `tail` 1..200 (default 60); `filter` is a case-insensitive
substring (all lines when empty). Error lines (`[Error`, `[Fatal`,
`Exception`, `Error:`) are returned separately, the last 20. Lines are cut at
300 characters and the user profile path is replaced by `%USERPROFILE%`.
Runs for out-of-scope games that SpecTuner has state for, like `changes`.

Data: `{ "source", "file": "LogOutput.log", "lastWritten", "totalLines", "lines": [], "errors": [], "errorCount", "truncated" }`.

### `search-code`
Request: `{ "gamePath": "…", "query": "jump", "kind": "any" | "name" | "text", "limit": 20, "assembly": null }`

Changes nothing in the game folder. `query` at least 2 characters; `limit`
1..50 (default 20). Without `assembly` it searches the game's own assemblies:
every managed dll in `Managed/` except engine, framework and loader ones
(`UnityEngine*`, `Unity.*`, `System*`, `mscorlib`, `netstandard`,
`Microsoft.*`, `Mono.*`, `Newtonsoft.*`, `BepInEx*`, `0Harmony*`,
`HarmonyX*`, `MonoMod.*`; the table is `GameAssemblies.EngineAndFramework`).
`assembly` (file name with or without `.dll`, or assembly name) targets one
assembly, engine ones included. Each assembly is decompiled on first use into
the cache above; the test game's `Assembly-CSharp` (120 types) takes about 3.5 s.

- `name`: type and member names containing the query (case-insensitive), exact
  matches first, then prefixes, then the rest.
- `text`: decompiled source lines containing the query.
- `any`: name hits first, then text hits on other lines.

Data:
```jsonc
{ "hits": [ { "assembly": "Assembly-CSharp", "type": "SampleGame.PlayerController", "member": "Update",
              "signature": "private void Update()", "line": 231,
              "snippet": "_verticalVelocity = Mathf.Sqrt(2f * jumpHeight * (0f - gravity));",   // text hits only, ≤ 160 chars
              "match": "name" | "text" } ],
  "total": 45, "truncated": true,
  "assemblies": [ { "name": "Assembly-CSharp", "types": 120, "fromCache": false, "decompileSeconds": 3.5 } ],
  "seconds": 3.9 }
```
`line` is a line in the decompiled file of the hit's top-level type, the same
numbering `read-code` uses. Type names use `.` for nesting and no generic arity.

### `read-code`
Request: `{ "gamePath": "…", "type": "SampleGame.PlayerController", "member": "Update", "maxLines": 200, "startLine": null, "assembly": null }`

`type` may be a full name, a simple name, `Outer+Inner`, `Pool`1` or
`Pool<T>`. A simple name shared by several types gives `AMBIGUOUS_TYPE` with
candidates; an unknown name gives `TYPE_NOT_FOUND` with the nearest names.
`maxLines` 10..400 (default 200). Code comes back as one string with each line
prefixed `"<line>: "`.

- With `member`: every declaration with that name (all overloads), up to
  `maxLines` in total. `ctor`/`.ctor` means the constructors. Data:
  `{ "assembly", "type", "members": [ { "name", "kind", "signature", "from", "to" } ], "code", "truncated" }`.
- Without `member`, type fits in `maxLines` (or `startLine` given): lines
  `from`..`to` of the type. Data: `{ "assembly", "type", "kind", "signature", "from", "to", "typeLines": { "from", "to" }, "code", "truncated", "note" }`;
  `note` says which `startLine` to ask for next.
- Without `member`, type too long: an outline. Data:
  `{ "assembly", "type", "kind", "signature", "typeLines", "fields": ["44: private float jumpHeight", …], "members": ["153: private void Update()", …], "nestedTypes": [ … ], "note" }`.

### `build-plugin`
Request:
```jsonc
{ "gamePath": "…", "name": "DoubleJump", "title": "Double the jump height",
  "description": "…", "source": "using BepInEx; …", "test": true }
// or "sources": [ { "file": "Plugin.cs", "code": "…" }, … ]   (1..20 files, names like Foo.cs)
```
`name`: letters and digits, starting with a letter, at most 40 characters, not
"SpecTuner". `title` is required. `test` (default `true`): start the game once
after installing the mod to check that it loads (see "Test launch" below).

Markdown fences: when a source text, ignoring leading and trailing whitespace,
starts with a fence line (three backticks, optionally a language such as
`csharp`) and ends with a line of three backticks, those two lines are replaced
by empty lines before compiling. Nothing else is changed, so every line keeps the
number it had in the text that was sent: compile errors, safety refusals and the
saved source all use the sender's line numbers. Fences elsewhere (in strings or
comments), or an opening fence without a closing one, are left alone. Data
`strippedCodeFence` lists the files this happened to.

Steps, all before the game folder is touched:

1. Compile in-process (C# 9) against the game's `Managed/*.dll` and
   `BepInEx/core/*.dll` (`0Harmony20.dll` is left out: it duplicates every
   Harmony type). The assembly is named `SpecTuner.Mod.<name>`.
2. Safety check (`UNSAFE_CODE`) on the compiled semantic model. The deny list
   is one table, `SafetyCheck.Rules`, with a plain reason per entry: process
   start; networking (`System.Net.*`, `UnityEngine.Networking.*`, `WWW`,
   `WWWForm`, `Application.OpenURL`); the registry (`Microsoft.Win32.*`);
   native code (`DllImport`, `Marshal`, `NativeLibrary`, `extern`, `unsafe`);
   loading or generating code (`Assembly.Load*`, `System.Reflection.Emit.*`,
   `System.Runtime.Loader.*`, `System.CodeDom.Compiler.*`, `Microsoft.CSharp.*`,
   `AppDomain` loading and execution, `Activator.CreateInstanceFrom`,
   `dynamic`); files (`File`, `Directory`, `FileInfo`, `DirectoryInfo`,
   `FileSystemInfo`, `FileStream`, `StreamReader`, `StreamWriter`,
   `FileSystemWatcher`, `DriveInfo`); `Environment.Exit`, `FailFast`,
   `SetEnvironmentVariable`; threads and timers (`Thread`, `ThreadPool`,
   `System.Threading.Timer`, `System.Timers.Timer`, `Task.Run`,
   `TaskFactory.StartNew`). Symbols are resolved, so `using` aliases,
   `using static` and fully qualified names are caught. Reflection
   (`System.Reflection` reads and invokes), `Path`, `MemoryStream` and all of
   HarmonyLib are allowed. Not caught: anything reached only through
   reflection strings (`Type.GetType("System.IO.File").GetMethod("Delete").Invoke(…)`),
   and game or Unity APIs that themselves touch files or the network.
3. Compile errors (`COMPILE_FAILED`), with hints for private access (CS0122),
   unknown types or names (CS0246, CS0234, CS0103), unknown members (CS1061,
   CS0117), obsolete APIs (CS0618, CS0619) and bad overrides (CS0115).
   When the unknown type or name is a well-known one, the hint names the exact
   using (table `PluginCompiler.WellKnownTypes`): `HashSet`, `List`,
   `Dictionary`, `Queue`, `Stack`, `IEnumerable<T>` → `System.Collections.Generic`;
   `IEnumerator` → `System.Collections`; `Enumerable` → `System.Linq`;
   `StringBuilder` → `System.Text`; `Action`, `Func`, `Math`, `Exception`, `Type`
   → `System`; `MethodInfo`, `FieldInfo`, `PropertyInfo`, `BindingFlags` →
   `System.Reflection`; `Keyboard`, `Mouse`, `Gamepad` →
   `UnityEngine.InputSystem` (or, when the game does not ship the Input System,
   advice to use `UnityEngine.Input`); `ManualLogSource` → `BepInEx.Logging`;
   and common BepInEx, Harmony and UnityEngine types. A LINQ method (`Where`,
   `Select`, `ToList`, …) missing on a sequence (CS1061) gets "add
   `using System.Linq;`".
   Private and internal members of the referenced assemblies are imported (and
   stay unusable), so using a game's private member directly gives CS0122, not
   CS1061, and its hint names the member and how to reach it: for a field, the
   patch parameter `ref <Type> ___<name>` or
   `AccessTools.FieldRefAccess<Owner, Type>("name")`; for a method,
   `AccessTools.Method`; for a property, `Traverse`/`AccessTools.Property`.
   CS1061/CS0117 now only means the member really does not exist.
4. Plugin shape (`INVALID_PLUGIN`): exactly one non-abstract, non-generic class
   with `[BepInPlugin("guid", "Name", "1.0.0")]` deriving from
   `BaseUnityPlugin`, plain-text arguments, a version like `1.0.0`; the GUID
   must not be the probe's or another live mod's.

Then, like `install`: lock, refuse with `GAME_RUNNING`, install the loader and
probe if needed, snapshot, write `BepInEx/plugins/SpecTuner/<name>.dll` and
record one `mod` change (`title`, `description`, `files`). The source is kept
under `mods/<name>/<changeId>/`. Building the same `name` again is a new change
that replaces the dll; undoing it brings the previous build back.

Data:
```jsonc
{ "changeId": "c0003", "file": "BepInEx/plugins/SpecTuner/DoubleJump.dll", "replaces": false,
  "plugin": { "guid": "spectuner.mod.doublejump", "name": "Double Jump", "version": "1.0.0" },
  "lookFor": "Double Jump 1.0.0",           // the entry to expect in launch-and-benchmark's loadedPlugins
  "strippedCodeFence": [],
  "test": { "ran": true, "started": true, "loaded": true, "errorCount": 0, "errors": [],
            "logLines": [ "[Info   :Double Jump] loaded" ],   // this run's lines logged by the mod itself, ≤ 10, newest last
            "seconds": 14.2 } }                               // the test launch's wall time
// with "test": false: "test": { "ran": false }
```

#### Test launch

With `test` true (the default), after the mod is installed, under the same
lock, the game is started once with a short probe run (2 s warm-up, 3 s
measuring, 90 s to start) and closed, so the plugin loads and its `Update`
patches run for a few seconds. This run is not a benchmark: it is not added to
`benchmarks.json`, so it is never the "previous test" or the baseline of a
`launch-and-benchmark` comparison, and its result file is deleted. The mod's
status comes from this run's logs, read as for `launch-and-benchmark`'s
`mods` (below); `logLines` are the BepInEx log lines whose source tag is the
plugin's name.

- The game started, the mod loaded (its "Name Version" is in the log) and no
  error belongs to it: every live change is marked `verified`, and the result is
  the success above. The summary says SpecTuner started the game once and the mod
  loaded without errors.
- The game did not start, crashed, exited or timed out: exactly what
  `launch-and-benchmark` does (same code): every unverified change, the new mod
  included, is reverted, and the result is `GAME_FAILED_TO_START` with the same
  message and data (`runId` is `modtest-<changeId>`).
- The game started but the mod did not load, or errors belong to it: the mod's
  change is undone (a rebuild's undo brings the previous build back), the other
  live changes are marked `verified`, and the result is `ok: false` with
  `MOD_FAILED_TEST`. The message is plain and carries the first error, e.g.
  "The mod was built, but it logged 2 error(s) when the game started, so it was
  removed again. First error: …" or "The mod was built, but the game did not load
  it, so it was removed again.", plus "The previous version of the mod is back in
  place." when one is. Data:
  `{ "changeId", "loaded", "errorCount", "errors" (first 5), "logLines" (≤ 10), "removed": true, "restoredPrevious" }`.

`mods/<name>/plugin.json` always describes the build that is in place: when a
build is undone by its test launch, the previous record is put back (or removed
when there was none).

Testing a mod again later is a `launch-and-benchmark` with a short window. Its `mods`
list says for every live mod whether BepInEx loaded it (`loaded`: its
"Name Version" is in `loadedPlugins`), and `pluginErrors` gives, per plugin
name, up to 5 error entries from this run's logs that the plugin logged
itself (an error line with its source tag) or whose stack trace names one of
its types or its assembly, or error lines naming "[Name Version]" (BepInEx's
"Error loading [Name Version]"). The logs are BepInEx's `LogOutput.log` and,
when written during the run, Unity's `Player.log` (see `read-logs`): BepInEx 5
by default (`WriteUnityLog = false`) leaves Unity's own messages out of
`LogOutput.log`, so an exception thrown in a plugin's `Awake` or `Update` only
shows up in `Player.log`. There an exception follows whatever line came before
it, which is why an error after a mod's info line only counts for that mod when
the stack trace names it. Such an entry is reported as the exception line plus
the stack frame naming the mod, without the unrelated line before it, so one
exception thrown every frame is listed once. BepInEx catches exceptions thrown while a plugin loads or patches, so
the game still starts; a mod with `loaded: false` or entries in `pluginErrors`
should be reverted or fixed. `read-logs` with `filter` set to the plugin's name
returns its own lines.
