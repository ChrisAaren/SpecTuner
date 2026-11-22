# Third-party notices

SpecTuner itself is released under the MIT licence (see [LICENSE](LICENSE)).
This file lists the third-party components SpecTuner ships or installs into a
game, with the licence each was checked against. Licence texts for the mod
loader parts are in [licenses/](licenses/); the build copies this file and
that folder into the app.

Re-check this list before every release build, and update it whenever a
shipped dependency is added or changes version.

The Windows, Linux and macOS builds ship the same components. Where a package
has per-system native parts (Electron itself, and npm packages such as the file
watcher), each build carries its own system's part of the same package and
version, under the same licence. The Linux packages and the macOS dmg are made
by electron-builder (a build tool, not shipped) and add no third-party code.

## Shipped inside the app

| Component | Version | Licence | Where it ends up | Checked against |
|---|---|---|---|---|
| opencode | 2.3.0, commit `dcb24c74dc` | MIT | SpecTuner's app code started from it: the desktop app, its UI and the bundled server program | `app/LICENSE` |
| Electron | 44.9.1 | MIT, plus Chromium's notices, which Electron ships as `LICENSES.chromium.html` | App shell | GitHub licence metadata for `electron/electron` |
| Bun runtime | the version that built the server program (1.4.4 or newer) | MIT for Bun itself; Bun's licence page lists the components it embeds and their licences | Embedded in the compiled server program `spectuner-server.exe` (`spectuner-server` on Linux and macOS) | Bun's `LICENSE.md` |
| npm packages used by the app and server | see `app/bun.lock` | mostly MIT; see each package's licence field | Bundled into the app and the server program | Not individually listed here; review before a release |
| .NET runtime | 10.0 | MIT | Inside the self-contained sidecar exe | GitHub licence metadata for `dotnet/runtime` |
| Roslyn (`Microsoft.CodeAnalysis.CSharp`) | 5.12.0 (see `sidecar/src/SpecTuner.Sidecar/SpecTuner.Sidecar.csproj`) | MIT | Inside the sidecar exe; compiles mods | GitHub licence metadata for `dotnet/roslyn` |
| ICSharpCode.Decompiler | 11.2.0.10050 (same project file) | MIT | Inside the sidecar exe; reads game code | GitHub licence metadata for `icsharpcode/ILSpy` |

## Installed into a game folder by the sidecar

These come from three unmodified upstream release archives of BepInEx
5.4.23.6, one per system, all kept at `sidecar/assets/bepinex/` and embedded
in every sidecar build. The sidecar checks each archive against its SHA-256
before use, installs the one for the game's system, and "Restore game"
removes it again. No archive's `changelog.txt` is ever extracted.

| Archive | For | SHA-256 |
|---|---|---|
| `BepInEx_win_x64_5.4.23.6.zip` | Windows x64 games | `6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755` |
| `BepInEx_linux_x64_5.4.23.6.zip` | Linux x64 games | `f8ac87a97fb1ef85451c04ace1fddb7dd7eb0b079c179b989d6bc462c98bd365` |
| `BepInEx_macos_universal_5.4.23.6.zip` | macOS games | `2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638` |

All three contain the same `BepInEx/core`. They differ only in the Doorstop
files at the game root and, on Linux and macOS, BepInEx's start script. The
archives themselves contain no licence texts; the texts in
[licenses/](licenses/) cover all three.

| Component | Version | Licence | Files | Checked against |
|---|---|---|---|---|
| BepInEx | 5.4.23.6 | MIT | `BepInEx/core/BepInEx*.dll`; `run_bepinex.sh` (BepInEx's start script, Linux and macOS archives) | `LICENSE` at tag `v5.4.23.6` (the 5.x line is MIT; the 6.x line is LGPL-2.1) |
| UnityDoorstop | 4.6.0 | LGPL-2.1 | Windows `winhttp.dll`, `doorstop_config.ini`; Linux `libdoorstop.so`; macOS `libdoorstop.dylib`; `.doorstop_version` in all three | `LICENSE` at tag `v4.6.0` |
| HarmonyX | bundled with BepInEx | MIT | `BepInEx/core/0Harmony*.dll`, `HarmonyXInterop.dll` | `LICENSE` in `BepInEx/HarmonyX` |
| MonoMod | bundled with BepInEx | MIT | `BepInEx/core/MonoMod.*.dll` | `LICENSE` in `MonoMod/MonoMod` |
| Mono.Cecil | bundled with BepInEx | MIT | `BepInEx/core/Mono.Cecil*.dll` | `LICENSE.txt` in `jbevain/cecil` |

UnityDoorstop is LGPL-2.1. On every system it is shipped as the unmodified
upstream binary from BepInEx's release archive (`winhttp.dll`,
`libdoorstop.so`, `libdoorstop.dylib`), as a separate file the player can
replace, with its licence text included.
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
