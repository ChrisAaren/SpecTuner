# SpecTuner sidecar

`spectuner-sidecar` (`.exe` on Windows) does all Unity-specific work for SpecTuner: detecting
a game and whether it is in scope, snapshots and undo, installing BepInEx and
the probe plugin, changing quality settings, benchmarks, reading the game's
code, building and testing mods, reading logs, and putting past changes back.
One JSON request in, one JSON envelope out.

The interface, including every command, error code and state file, is
[`docs/sidecar-contract.md`](../docs/sidecar-contract.md). The rules the
sidecar must follow are in [`CLAUDE.md`](../CLAUDE.md), and the reasons
behind its design in [`docs/design-decisions.md`](../docs/design-decisions.md).

## Requirements

- Native Unity Mono games on Windows x64, Linux x64 and macOS (Apple Silicon, and Intel for games with an x86_64 build).
  All platform differences live in `src/SpecTuner.Sidecar/Platform/` (see "Platforms" in the contract).
- .NET SDK 10.0 to build. Players need nothing: the published program is
  self-contained.
- Git Bash for `publish.sh`.

## Build, test, publish

```sh
# in sidecar/
dotnet build SpecTuner.Sidecar.slnx
dotnet test SpecTuner.Sidecar.slnx
./publish.sh
```

`dotnet test` takes about 45 seconds once built and should report
`Passed! - Failed: 0`. A few tests apply to one system only; elsewhere they are
reported as skipped with the reason (on Windows: 6, the Linux and macOS process, file-mode and linked-folder tests).

`publish.sh [win-x64|linux-x64|osx-arm64|osx-x64]` works from any folder. The
default, `win-x64`, writes `artifacts/sidecar/spectuner-sidecar.exe` at the
repository root (about 92 MB); the others write
`artifacts/sidecar/<rid>/spectuner-sidecar`. Published on Windows, a Linux or
macOS program has no execute bit and no signature: `chmod +x` it, and sign an
`osx-arm64` one on a Mac (`codesign -s -` at least). It runs (here for win-x64):

```sh
dotnet publish src/SpecTuner.Sidecar/SpecTuner.Sidecar.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false -p:DebugType=none -o ../artifacts/sidecar
```

Trimming stays off because of Roslyn and the decompiler. The repository's `build.sh` runs `publish.sh` and bundles the exe
into the app.

## Commands

| Command | What it does |
|---|---|
| `detect` | Names the game, its Unity version and backend, whether it is supported and what SpecTuner has installed or changed. Changes nothing |
| `install` | Takes the baseline and installs BepInEx and the probe plugin. Other changing commands do this by themselves when needed |
| `changes` | Lists the changes in place, newest first, and past changes that can be applied again |
| `revert` | Undoes one change, or everything ("Restore game") |
| `reapply` | Puts a past quality change or mod back as a new change |
| `set-quality` | Saves quality setting overrides that the probe applies in the game |
| `launch-and-benchmark` | Starts the game, measures frame times and closes it; with `trySettings`, compares with and without settings inside one launch |
| `read-logs` | Reads the BepInEx log or the game's own `Player.log` from the last run |
| `search-code` | Searches the game's decompiled code by name or text |
| `read-code` | Reads one member, a whole short type, or an outline of a long one |
| `build-plugin` | Checks and compiles a mod, installs it as one change, and starts the game once to test it |
| `play` | Starts the game for the player, with or without SpecTuner's changes, and returns at once |
| `diagnose` | A read-only report on the system and a game folder, without paths or names, for bug reports |

Examples, on a copy of a game:

```sh
# in the repository root, after ./sidecar/publish.sh
exe=artifacts/sidecar/spectuner-sidecar.exe
echo '{"gamePath":"C:/Games/SampleGame"}' | $exe detect
echo '{"gamePath":"C:/Games/SampleGame","label":"before"}' | $exe launch-and-benchmark
echo '{"gamePath":"C:/Games/SampleGame","trySettings":{"shadows":"Disable"}}' | $exe launch-and-benchmark
$exe revert --request request.json
```

`SPECTUNER_HOME` overrides the state folder. The defaults are
`%LOCALAPPDATA%\SpecTuner` on Windows, `~/Library/Application Support/SpecTuner`
on macOS and `${XDG_DATA_HOME:-~/.local/share}/SpecTuner` on Linux. Per-game
state lives in `games/<id>/` below it, never in the game folder.

## Layout

```
sidecar/
  SpecTuner.Sidecar.slnx
  nuget.config                  nuget.org only, so builds do not depend on a developer's own NuGet sources
  publish.sh                    builds artifacts/sidecar/spectuner-sidecar.exe (or <rid>/spectuner-sidecar)
  assets/bepinex/               BepInEx 5.4.23.6 zips for win x64, linux x64, macos universal, unmodified (all embedded)
  src/SpecTuner.Sidecar/        net10.0 console app, assembly spectuner-sidecar
    Program.cs                  UTF-8 stdin and stdout, calls CommandHost
    Cli/                        CommandHost (registry and dispatch), envelope, request, error codes
    Commands/                   one class per command, plus shared steps (GameSetup, GameRun, PastChanges)
    Compilation/                PluginCompiler: Roslyn, C# 9, against a game's Managed and BepInEx core;
                                SafetyCheck: the deny table for generated mods
    Code/                       DecompiledCache (ICSharpCode.Decompiler, per-assembly cache and index),
                                CodeSearch, CodeIndex
    Probe/                      probe plugin source, embedded and compiled per game at install;
                                FrameStats.cs and MiniJson.cs are also compiled into the sidecar
    Quality/                    QualitySettingsFile: valid keys, ranges, merge, the overrides file
    Platform/                   everything that differs per system: Platforms (profile from the host or
                                SPECTUNER_PLATFORM), layouts (WindowsLayout, LinuxLayout, MacLayout),
                                ExecutableHeader (ELF, Mach-O), LoaderKit (package, files, launch),
                                UserFolders, game processes (JobObject on Windows, signals and
                                running.json on Linux and macOS), HostFiles, HostFacts
    Benchmark/                  GameLauncher (start, wait, close),
                                BenchmarkHistory, Verdict (the trial rules)
    Logs/                       LogReader: filtering, errors, plugin list, user path scrubbing
    Games/                      GameInspector (scope gate), Signatures (anti-cheat and networking tables),
                                PeFile, GameProcesses
    State/                      SidecarHome, GameState, Baseline, Ledger, Snapshot, GameRestorer,
                                GameTree, OurFiles (the only places written in a game folder), AtomicFile
    Loader/                     BepInExPackage (embedded zip), LoaderInstaller,
                                BepInExConfig (Unity 6000+ entrypoint), ProbeInstaller
  tests/SpecTuner.Sidecar.Tests/  MSTest; synthetic game folders in a temp folder
  tests/SpecTuner.FakeGame/       stand-in game exe for launch tests
```

Packages: `ICSharpCode.Decompiler` and `Microsoft.CodeAnalysis.CSharp` in the
sidecar, MSTest in the test project. Nothing else; ask before adding one.

## Adding a command

1. Add a class in `Commands/` that implements `ICommand` and starts with
   `GameCommands.RequireInScope(request)`.
2. Register it in `Cli/CommandHost.cs`; add error codes to `Cli/ErrorCodes.cs`.
3. Wrap changes to the game folder in `Ledger.Begin`, then `Commit` or
   `Abandon`, so a snapshot is taken and the change can be undone.
4. Document it in `docs/sidecar-contract.md` and add tests.

To make it available to the modder agent, see "Expose a sidecar command to the
agent as a tool" in `CLAUDE.md`.

## How the tests work

Tests never launch a real game. They build fake game folders shaped like a
Windows, Linux or macOS game (`GameFixture.Create`): minimal PE, ELF or Mach-O
headers, the test assembly as `Assembly-CSharp.dll`, a stub `UnityEngine.dll`
with the API the probe uses, and .NET's own mscorlib and netstandard facades,
so the real probe source compiles. Every test sets `SPECTUNER_HOME` to a temp
folder.

Linux and macOS logic runs on every system through the platform switch
(`SidecarTest.UsePlatform`, `CreateGame(GamePlatform.Linux)`): layout, scope gate,
install and restore, launch environment. Launch and `play` tests use
`SpecTuner.FakeGame` on the host's own layout, controlled by the environment
variables `SPECTUNER_FAKE_GAME` (`result`, `crash`, `hang` or `linger`),
`SPECTUNER_FAKE_GAME_FPS` and `SPECTUNER_FAKE_GAME_LAUNCH_FILE` (records the
arguments and loader variables it received). Tests for one system only carry
`[OSCondition]` with the reason.

Anything that needs a real game or BepInEx at run time, such as a mod really
loading, is not covered by `dotnet test`. Check it by hand on a copy of a
Unity Mono game, and use `scripts/compare-trees.sh` at the repository root to
confirm that "Restore game" leaves the copy identical to an untouched one.
