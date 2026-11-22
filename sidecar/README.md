# SpecTuner sidecar

`spectuner-sidecar.exe` does all Unity-specific work for SpecTuner: detecting
a game and whether it is in scope, snapshots and undo, installing BepInEx and
the probe plugin, changing quality settings, benchmarks, reading the game's
code, building and testing mods, reading logs, and putting past changes back.
One JSON request in, one JSON envelope out.

The interface, including every command, error code and state file, is
[`docs/sidecar-contract.md`](../docs/sidecar-contract.md). The rules the
sidecar must follow are in [`CLAUDE.md`](../CLAUDE.md), and the reasons
behind its design in [`docs/design-decisions.md`](../docs/design-decisions.md).

## Requirements

- Windows x64.
- .NET SDK 10.0 to build. Players need nothing: the published exe is
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
`Passed! - Failed: 0` (247 tests today).

`publish.sh` works from any folder and writes
`artifacts/sidecar/spectuner-sidecar.exe` at the repository root (about
92 MB). It runs:

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

Examples, on a copy of a game:

```sh
# in the repository root, after ./sidecar/publish.sh
exe=artifacts/sidecar/spectuner-sidecar.exe
echo '{"gamePath":"C:/Games/SampleGame"}' | $exe detect
echo '{"gamePath":"C:/Games/SampleGame","label":"before"}' | $exe launch-and-benchmark
echo '{"gamePath":"C:/Games/SampleGame","trySettings":{"shadows":"Disable"}}' | $exe launch-and-benchmark
$exe revert --request request.json
```

`SPECTUNER_HOME` overrides the state folder (default
`%LOCALAPPDATA%\SpecTuner`). Per-game state lives in `games/<id>/` below it,
never in the game folder.

## Layout

```
sidecar/
  SpecTuner.Sidecar.slnx
  nuget.config                  nuget.org only, so builds do not depend on a developer's own NuGet sources
  publish.sh                    builds artifacts/sidecar/spectuner-sidecar.exe
  assets/bepinex/               BepInEx 5.4.23.6 win x64 zip, unmodified (embedded in the exe)
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
    Benchmark/                  GameLauncher (start, wait, close), JobObject (kill on close),
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

Tests never launch a real game. They build fake game folders: minimal PE
headers, the test assembly as `Assembly-CSharp.dll`, a stub `UnityEngine.dll`
with the API the probe uses, and .NET's own mscorlib and netstandard facades,
so the real probe source compiles. Every test sets `SPECTUNER_HOME` to a temp
folder.

One test starts a copy of `PING.EXE`, renamed to the fake game's exe, to check
`GAME_RUNNING`. Launch tests use `SpecTuner.FakeGame`, controlled by the
environment variables `SPECTUNER_FAKE_GAME` (`result`, `crash` or `hang`) and
`SPECTUNER_FAKE_GAME_FPS`.

Anything that needs a real game or BepInEx at run time, such as a mod really
loading, is not covered by `dotnet test`. Check it by hand on a copy of a
Unity Mono game, and use `scripts/compare-trees.sh` at the repository root to
confirm that "Restore game" leaves the copy identical to an untouched one.
