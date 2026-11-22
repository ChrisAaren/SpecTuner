# Third-party notices

SpecTuner itself is released under the MIT licence (see [LICENSE](LICENSE)).
This file lists the third-party components SpecTuner ships or installs into a
game, with the licence each was checked against. Licence texts for the mod
loader parts are in [licenses/](licenses/); the build copies this file and
that folder into the app.

Re-check this list before every release build, and update it whenever a
shipped dependency is added or changes version.

## Shipped inside the app

| Component | Version | Licence | Where it ends up | Checked against |
|---|---|---|---|---|
| opencode | 2.3.0, commit `dcb24c74dc` | MIT | SpecTuner's app code started from it: the desktop app, its UI and the bundled server program | `app/LICENSE` |
| Electron | 44.9.1 | MIT, plus Chromium's notices, which Electron ships as `LICENSES.chromium.html` | App shell | GitHub licence metadata for `electron/electron` |
| Bun runtime | the version that built the server program (1.4.4 or newer) | MIT for Bun itself; Bun's licence page lists the components it embeds and their licences | Embedded in the compiled server program `spectuner-server.exe` | Bun's `LICENSE.md` |
| npm packages used by the app and server | see `app/bun.lock` | mostly MIT; see each package's licence field | Bundled into the app and the server program | Not individually listed here; review before a release |
| .NET runtime | 10.0 | MIT | Inside the self-contained sidecar exe | GitHub licence metadata for `dotnet/runtime` |
| Roslyn (`Microsoft.CodeAnalysis.CSharp`) | 5.12.0 (see `sidecar/src/SpecTuner.Sidecar/SpecTuner.Sidecar.csproj`) | MIT | Inside the sidecar exe; compiles mods | GitHub licence metadata for `dotnet/roslyn` |
| ICSharpCode.Decompiler | 11.2.0.10050 (same project file) | MIT | Inside the sidecar exe; reads game code | GitHub licence metadata for `icsharpcode/ILSpy` |

## Installed into a game folder by the sidecar

All of these come from one unmodified upstream archive,
`BepInEx_win_x64_5.4.23.6.zip` (SHA-256
`6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755`), kept at
`sidecar/assets/bepinex/`, embedded in the sidecar, and removed again by
"Restore game". The archive's `changelog.txt` is never extracted.

| Component | Version | Licence | Files | Checked against |
|---|---|---|---|---|
| BepInEx | 5.4.23.6 | MIT | `BepInEx/core/BepInEx*.dll` | `LICENSE` at tag `v5.4.23.6` (the 5.x line is MIT; the 6.x line is LGPL-2.1) |
| UnityDoorstop | 4.6.0 | LGPL-2.1 | `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` | `LICENSE` at tag `v4.6.0` |
| HarmonyX | bundled with BepInEx | MIT | `BepInEx/core/0Harmony*.dll`, `HarmonyXInterop.dll` | `LICENSE` in `BepInEx/HarmonyX` |
| MonoMod | bundled with BepInEx | MIT | `BepInEx/core/MonoMod.*.dll` | `LICENSE` in `MonoMod/MonoMod` |
| Mono.Cecil | bundled with BepInEx | MIT | `BepInEx/core/Mono.Cecil*.dll` | `LICENSE.txt` in `jbevain/cecil` |

UnityDoorstop is LGPL-2.1. It is shipped as the unmodified upstream binary,
as a separate file the player can replace, with its licence text included.
Source: https://github.com/NeighTools/UnityDoorstop (tag `v4.6.0`).

## Used only to build or test (not shipped)

| Component | Licence |
|---|---|
| MSTest 4.0.2 (sidecar tests) | MIT |
| Bun, the .NET SDK, electron-builder | Build tools; only the parts listed above are redistributed |

## Not redistributed

- Unity engine assemblies are never shipped. Mods and the probe plugin are
  compiled on the player's machine against the game's own files.
- Games are never shipped or included in this repository.

## SpecTuner's own assets

The app's icon and logo are SpecTuner's own, made with Blender from
`branding/make_icon.py` with no external assets, fonts or textures.
