# CLAUDE.md

Guide for developers and coding agents working on SpecTuner. Read "Rules that
are not negotiable" before changing anything. `AGENTS.md` points here.

## Contents

- [What SpecTuner is](#what-spectuner-is)
- [Rules that are not negotiable](#rules-that-are-not-negotiable)
- [Architecture](#architecture)
- [Set up from a fresh clone](#set-up-from-a-fresh-clone)
- [Testing](#testing)
- [Conventions](#conventions)
- [Where to read more](#where-to-read-more)

## What SpecTuner is

SpecTuner is a Windows desktop app that changes Unity games for players who
own them. The player types a request in plain language, such as "make this run
smoothly on my laptop" or "double the jump height". An AI agent inside the app
measures the game, changes its graphics settings, or writes, builds and tests
a small mod, and every change can be undone with one click.

It is for non-technical players. They never see a terminal, a decompiler or
source code. The whole experience is one window:

1. Choose the game's folder.
2. Connect a model: an API key for a provider, or a local OpenAI-compatible
   endpoint. No provider is hard-coded.
3. Type a request.
4. Watch plain-language progress, then play.
5. Undo any single change, or restore the game completely.

Before changing how something works, read the matching part of
[docs/development.md](docs/development.md): it has step-by-step recipes for
the common changes (a sidecar command, a tool, the prompt, the Changes panel,
what the server refuses, the icon, the first-run screens).

### Terms that are easy to confuse

| Term | Meaning |
|---|---|
| Modder agent | The agent inside the finished app that changes games for the player. A product feature, defined in `app/packages/modder`. Not the coding agent working on this repository |
| Sidecar | `spectuner-sidecar.exe`, the C# helper program in `sidecar/`. Does all Unity-specific work. JSON in, JSON out |
| Probe plugin | SpecTuner's own BepInEx plugin. Reports hardware, logs frame times and applies quality settings inside the running game |
| BepInEx plugin, mod | A C# DLL that BepInEx loads into a Unity game. A "mod" is a BepInEx plugin the modder agent wrote |
| Server plugin, custom tool | TypeScript that extends SpecTuner's local server. The ten `game_*` tools are one server plugin, `app/packages/modder/plugin/` |
| GUI plugin | A window-side feature behind the app's plugin SDK. The Changes panel is one, in `app/packages/gui-plugins/src/spectuner-changes/` |

## Rules that are not negotiable

Each rule protects a player's game, data or PC. A change that breaks one is
wrong even if every test passes.

### Scope

- **SpecTuner supports Unity games built with the Mono backend,
  single-player or offline, on Windows x64.** Everything else is detected and declined
  politely, with a plain explanation: games with anti-cheat, games with
  DRM-protected or encrypted code, online multiplayer games, IL2CPP games and
  non-Unity games. The reason is that changing these is either unsafe for the player
  (bans, broken saves, cheating against other people) or not possible with
  this design. The checks live in the sidecar's scope gate
  (`sidecar/src/SpecTuner.Sidecar/Games/`), described in the contract.
- **IL2CPP is deferred, not rejected.** Keep the design open for it. The
  intended first step is settings-only tuning, with code mods later through
  Il2CppInterop and Cpp2IL. Do not build it without agreeing the plan first.

### Protection

- **Never write code that bypasses, disables or works around anti-cheat, DRM,
  licence checks or encryption.** This applies to the app, to the sidecar and
  to anything the modder agent may produce. The sidecar only detects these;
  the agent's prompt tells it to decline such requests.

### Game files

- **Never edit or replace original game files.** Every change lives in the
  BepInEx folder SpecTuner adds, or in SpecTuner's own config files. The reason is that
  the player's game must always be recoverable, and a game update or a file
  check by the store must not find altered files.
- **Snapshot before every change.** The sidecar copies its own files before
  each change, so any single change can be undone.
- **Revert automatically when the game fails to start after a change**, and
  tell the player in plain words.
- **Every change is undoable on its own** and appears in the Changes panel
  with its own Undo.
- **"Restore game" leaves the folder as it was found.** It removes everything
  SpecTuner added. Files the game itself created later, such as saves, are
  left in place.

### The modder agent

- **It may call only the `game_*` tools.** No shell, no file edits, no web, no
  other agents, skills or tool servers. The reason is that the agent acts on a player's PC
  for a player who cannot judge what it does.
- **Tool output stays small.** Tools return search hits, signatures,
  summaries and outlines, never whole decompiled files. The reason is that small and
  local models have small context windows.
- **Performance claims must be measured.** Benchmark, change, benchmark again,
  and keep only changes that measurably help. The agent never says a change
  works unless a tool result shows it.
- **It explains what it did in plain language**, with no code shown by
  default.

### Dependencies

Do not add a dependency without agreeing it first. Never add native reverse
engineering tools (Ghidra, Frida and the like), external MCP servers or
third-party command-line tools. The sidecar uses only
`ICSharpCode.Decompiler`, `Microsoft.CodeAnalysis.CSharp` (Roslyn) and
`System.Text.Json`, plus MSTest in its test project.

## Architecture

```
Desktop app (Electron window, app/packages/desktop + app/packages/app)
        |  HTTP on 127.0.0.1, password protected
SpecTuner server (app/packages/server, core, ai): agent loop, sessions, model providers
        |
Modder agent  --calls-->  game_* tools (server plugin, TypeScript, thin wrappers)
                                |  one process per call, JSON on stdin and stdout
                          Sidecar (C#, spectuner-sidecar.exe)
                                |
                    Game folder: BepInEx + probe plugin + generated mods
```

All Unity logic lives in the sidecar. The TypeScript side passes requests
through and shows results. How a request flows through these parts, how the
lock-down is layered, and where state lives on disk are in
[docs/development.md](docs/development.md).

### What lives where

| Part | Path |
|---|---|
| Sidecar program | `sidecar/src/SpecTuner.Sidecar/` (one class per command in `Commands/`, registry in `Cli/CommandHost.cs`, error codes in `Cli/ErrorCodes.cs`) |
| Sidecar tests | `sidecar/tests/SpecTuner.Sidecar.Tests/` (MSTest, fake games in `Support/`), `sidecar/tests/SpecTuner.FakeGame/` (stand-in game exe) |
| Sidecar interface | `docs/sidecar-contract.md` (commands, envelope, error codes, state files) |
| Probe plugin | `sidecar/src/SpecTuner.Sidecar/Probe/ProbePlugin.cs`, embedded in the sidecar as source and compiled per game |
| Mod safety check | `sidecar/src/SpecTuner.Sidecar/Compilation/SafetyCheck.cs` |
| Bundled BepInEx | `sidecar/assets/bepinex/` (unmodified upstream zip, embedded in the sidecar) |
| App code | `app/`, a Bun workspace. Packages in `app/packages/` |
| Modder agent: tools | `app/packages/modder/plugin/` (`tools.ts` definitions, `display.ts` plain titles, `sidecar.ts` runs the sidecar, `agents.ts` agent lock, `changes.ts` and `rpc.ts` for the Changes panel, `local-model.ts` for the local model form) |
| Modder agent: prompt | `app/packages/modder/src/prompt/modder.txt` |
| Modder agent: lock-down | `app/packages/modder/src/config.ts` (config document), `src/env.ts` (server environment) |
| Modder dev scripts | `app/packages/modder/script/` (`serve.ts`, `tools.ts`, `e2e.ts`, scripted model) |
| Server HTTP API and refusals | `app/packages/server/` (refusals in `src/middleware/lockdown.ts`) |
| Agent loop, sessions, permissions, providers, tool registry | `app/packages/core/src/` (`session/`, `permission/`, `provider.ts`, `tool/`, `plugin/`) |
| Model client | `app/packages/ai/` |
| Server program | `app/packages/cli/` (builds `spectuner.exe`; the app ships it as `spectuner-server.exe` and runs `serve`) |
| Client and API schema | `app/packages/client/`, `app/packages/protocol/` |
| Plugin API | `app/packages/plugin/` (`@spectuner/plugin`, for server and GUI plugins) |
| Desktop shell | `app/packages/desktop/` (Electron). SpecTuner wiring in `src/main/spectuner/` (server environment, upgrade step); resource staging in `script/stage-spectuner.ts` |
| Player UI | `app/packages/app/` (screens), `app/packages/session-ui/`, `app/packages/ui/`. SpecTuner's choices (hidden features, first-run, local model form, wording) in `app/packages/app/src/spectuner/` |
| Changes panel | `app/packages/gui-plugins/src/spectuner-changes/` |
| Icon and logo | `branding/` (see `branding/README.md`) |
| Build script | `build.sh` |
| Release build | `.github/workflows/release.yml` |
| Licence notices | `THIRD_PARTY_NOTICES.md`, `licenses/`, `LICENSE`, `app/LICENSE` |




## Set up from a fresh clone

### What is not in the repository

- **No game.** Bring your own Unity Mono game for end-to-end work, and work on
  a copy of it. `test-game/` and `test-game-pristine/` are git-ignored names
  for a working copy and an untouched reference copy, if you want to use them.
  `scripts/compare-trees.sh <a> <b>` compares two folders by relative path and
  SHA-256, which is how to check that "Restore game" left a copy identical to
  the reference.
- **No model key.** The app asks the player for one. Most tests need neither a
  game nor a model. They use fake games, a fake sidecar and a scripted model.
- **No build output.** `artifacts/`, `app/dist/` and the staged resources are
  produced by the build.

### Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Windows | x64 (built and tested on Windows 11) | The sidecar, tests that start processes and the packaging are Windows-only |
| Git for Windows | any recent | All commands below run in Git Bash |
| Bun | 1.4.4 or newer (the release build uses 1.4.5) | `app/package.json` pins `packageManager: bun@1.4.4` |
| .NET SDK | 10.0 | For the sidecar. Players need nothing: the sidecar is published self-contained |
| PowerShell 7 (`pwsh`) | optional | Only one desktop test needs it |
| Blender | 5.2 or later, optional | Only to regenerate the icon |

### Install, build and run

All commands run in Git Bash. Paths are relative to the repository root.

1. Install the app's dependencies.

   ```sh
   # in app/
   bun install
   ```

2. Build and test the sidecar. Success: `Passed! - Failed: 0`.

   ```sh
   # in sidecar/
   dotnet build SpecTuner.Sidecar.slnx
   dotnet test SpecTuner.Sidecar.slnx
   ```

3. Build everything and package the app. This publishes the sidecar, stages
   it with the plugin and prompt, builds the server program, builds and packs
   the desktop app. Close any running SpecTuner first. Success: the last line
   is the Windows path of `app\dist\desktop\app\SpecTuner.exe`.

   ```sh
   # in the repository root
   ./build.sh
   ```

   Options:

   - `--installer` also builds the installer
     (`app/dist/desktop/spectuner-desktop-win-x64.exe`);
   - `--skip-sidecar` reuses `artifacts/sidecar/spectuner-sidecar.exe`;
   - `--skip-cli` reuses the server program in `app/packages/cli/dist`.

   `SPECTUNER_VERSION` sets the version. The default is
   `2.3.0-spectuner.<UTC timestamp>`, unique per build so a new build replaces
   the previous build's background service.

4. Run the app unpackaged, after building once. It uses its own folders
   (`app-dev`, `com.spectuner.desktop.dev`) and port 29525, so it can run next
   to an installed SpecTuner.

   ```sh
   # in app/packages/desktop
   node_modules/electron/dist/electron.exe .
   ```

   Prefer this over `bun dev`. `bun dev` is the starting code base's
   development mode. It uses a different server channel and profile handling,
   and SpecTuner's builds are not checked with it.

### Run a server without the app

Useful for driving the agent from scripts. Runs the server from source with
the modder plugin, and the sidecar from `artifacts/sidecar/` unless
`--sidecar` names another. Pass `--home` with a scratch folder; without it the
server uses the app's real data folder.

```sh
# in app/packages/modder
bun run script/serve.ts --port 4900 --home "$(cygpath -w /tmp)/spectuner-dev"
```

Success: it prints `SpecTuner server http://127.0.0.1:4900 ... user spectuner
password <password>` and runs until Ctrl+C. Requests without the password get
401.

Two more scripts in the same folder:

```sh
# in app/packages/modder
# The tools the modder agent is really offered, from a real model request with a scripted model.
bun run script/tools.ts --game <folder>
# One real conversation with a real model, with live progress and a transcript.
bun run script/e2e.ts --game <folder> --model <provider/model> --prompt "double the jump height"
```

`tools.ts` works with any folder, even an empty one (then `game_detect`
answers `GAME_NOT_FOUND`); it should list exactly ten `game_*` tools. Run
`bun run script/e2e.ts --help` for its options (provider config file, API key
variables, a compiled server program). Point `e2e.ts` only at a copy of a
game.

### Package a release

`./build.sh --installer` builds the installer locally. The workflow
`.github/workflows/release.yml` does the same on a GitHub Windows runner: a
pushed tag `v<version>` builds with that version and publishes a release; a
manual run keeps the installer as a workflow artifact. Builds are not
code-signed. Re-check `THIRD_PARTY_NOTICES.md` before every release.

## Testing

Run each suite from its folder. Times are rough, on a typical developer PC,
with the code already built.

| Part | Folder | Command | Time | Result today |
|---|---|---|---|---|
| Sidecar | `sidecar/` | `dotnet test SpecTuner.Sidecar.slnx` | about 45 s | 247 pass |
| Modder agent and tools | `app/packages/modder` | `bun run test` | about 1 min | 120 pass (2 of them are skipped until the sidecar is published and the server program built) |
| Server | `app/packages/server` | `bun run test` | about 1 min | 166 pass, 9 skip |
| Desktop | `app/packages/desktop` | `bun run test` | about 10 s | 581 pass, 1 skip, 1 known failure |
| Changes panel | `app/packages/gui-plugins` | `bun run test` | 1 s | 15 pass |
| Server program | `app/packages/cli` | `bun run test` | about 40 s | 36 pass, 12 skip |
| SpecTuner UI | `app/packages/app` | see below | 1 s | 26 pass |
| Core | `app/packages/core` | `bun run test` | about 10 min | 6,778 pass, about 1,100 skip, 64 known failures |

No suite starts a real game or calls a real model. The sidecar tests build
fake game folders and use `SpecTuner.FakeGame` (controlled by
`SPECTUNER_FAKE_GAME` = `result`, `crash` or `hang`); the modder tests use a
fake sidecar and a scripted model. Anything that needs a real game, such as
whether a mod really loads, is checked by hand on a copy of a game.

### Known failures

- **Core: 64 tests fail on Windows in this code base**, the same ones every
  run: 55 shell tests (`ShellTool`, `ShellTool stopped`, `Session.shell`), 4
  plugin-reload tests (`config plugin reloads`), 2 `subagent idle reports`
  tests, 2 `Config` tests and 1 `Session.create` test. A run made while other
  suites were running on the same PC also failed some tests that use local
  ports or timing (GitLab OAuth callbacks), so run core on its own. To tell a
  new failure from a known one, record the
  failing names before your change and compare after it:

  ```sh
  # in app/packages/core, before your change
  bun run test 2>&1 | grep '^(fail)' | sed -E 's/ \[[0-9.]+m?s\]$//' | sort > "$TEMP/core-fail-before.txt"
  # after your change
  bun run test 2>&1 | grep '^(fail)' | sed -E 's/ \[[0-9.]+m?s\]$//' | sort > "$TEMP/core-fail-after.txt"
  comm -13 "$TEMP/core-fail-before.txt" "$TEMP/core-fail-after.txt"   # new failures
  ```

- **Desktop: one test needs PowerShell 7.** "installer grants sandbox access
  without changing unrelated permissions" runs `pwsh` and fails without it.

### The app package's tests

The app package needs its own flags. `bun run test:unit` and
`bun run test:browser` run all of its tests (thousands, a few minutes). For
SpecTuner's own screens only:

```sh
# in app/packages/app
bun test --conditions=solid --isolate --preload ../ui/test/solid-jsx.ts --preload ./happydom.ts ./src/spectuner
```

### Typechecks

Each package has its own typecheck. Success: `No type errors in ...`.

```sh
# in any package folder, for example app/packages/modder or app/packages/app
bun typecheck
```

Checked clean today: modder, server, cli, core, desktop, app, gui-plugins, ui
and session-ui.

### Before you hand in a change

Run the suites of every part you touched, plus their typechecks. If you
changed the tools, the prompt or the lock-down, also run the modder suite and
`script/tools.ts`. If you changed the server's routes, run
`app/packages/server/test/lockdown.test.ts`. If you changed what happens in a
game, try it on a copy of a real Mono game and confirm "Restore game" leaves
the copy identical to the untouched one with `scripts/compare-trees.sh`.


## Conventions

- **Code style.** TypeScript follows the Prettier settings in
  `app/package.json` (no semicolons, width 120). Several app packages carry
  their own `AGENTS.md` (`app`, `desktop`, `gui-plugins`, `ui`, `session-ui`,
  `plugin`, `cli`, `ai`, `schema`, `codemode`) with style and structure
  rules; follow them. They come from the starting code base, so ignore their
  references to its branches, hosted services and benchmarking against its
  repository. C# follows the existing sidecar code: one class per command,
  plain records, `System.Text.Json`.
- **Player-facing text is plain language.** No code, file paths, ids, stack
  traces or jargon in anything a player reads: sidecar summaries and
  messages, tool titles, panel text, the agent's replies. English only.
- **Logs, transcripts and work notes stay out of the repository.** Anything
  written by a running program or a working session can hold folder paths,
  user and computer names, hardware details and a game's code. `.gitignore`
  refuses the usual forms: `*.log`, transcripts and benchmark output, database
  and key files, local settings, and diary files such as `PROGRESS.md`,
  `DECISIONS.md` or `NOTES.md`. Put throwaway files in `tmp/`. Keep working
  notes in the pull request, and lasting design reasons in
  `docs/design-decisions.md`. Never use `git add -f` on an ignored file. In
  examples, tests and screenshots use made-up names and paths
  (`C:\Games\Sample Game`), never ones from your own machine.
- **Tests are not weakened to pass.** Do not delete, skip or loosen a test to
  make a change pass. Fix the code, or explain why the test was wrong.
- **Dependencies are not added casually.** Ask first, and check the licence.
  Update `THIRD_PARTY_NOTICES.md` for anything shipped.
- **Generated mods are checked before they are built.** Keep the safety
  check in front of every compile, including `reapply`. Every mod build also
  starts the game once to test it; the model cannot switch that off.
- **Measure, do not assume.** Performance changes are judged by the sidecar's
  in-launch trial, not by a single before and after.
- **Names say SpecTuner**: package scope `@spectuner/*`, `SPECTUNER_*`
  settings, `spectuner.db`, `spectuner.jsonc`, the `spectuner-server` program.
  These keep the opencode name on purpose; do not rename them:
  - third-party packages, such as `@opencode/pty` and its platform packages;
  - OpenCode's own model services ("OpenCode Go", "OpenCode Console", "OpenCode
    Zen"), their ids in the model catalog, and anything sent to them or to
    other model providers (headers, user agent);
  - web addresses;
  - recorded test data, snapshots, database migrations and the upstream
    changelog (`app/CHANGELOG.md`), which are identified by their content;
  - the licence text in `app/LICENSE` and the "started from opencode" credit;
  - the names the upgrade step looks for (`desktop/src/main/spectuner/upgrade.ts`).
- **Remove what the app does not use** rather than carrying it along, and
  keep the tests of what remains.

## Where to read more

| Document | What it holds |
|---|---|
| [docs/development.md](docs/development.md) | How a request flows, the lock-down layers, where state lives on disk, recipes for common changes, known gaps and good first tasks |
| [docs/design-decisions.md](docs/design-decisions.md) | Why each design choice was made, and what was considered and rejected |
| [docs/sidecar-contract.md](docs/sidecar-contract.md) | The sidecar's commands, JSON envelope, error codes and state files |
| [branding/README.md](branding/README.md) | The icon and logo, and how to rebuild them |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | Bundled components and their licences |
| [README.md](README.md) | What SpecTuner is and how to use it, for players |
