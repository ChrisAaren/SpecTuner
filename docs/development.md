# Working on SpecTuner in depth

This goes with [CLAUDE.md](../CLAUDE.md), which holds the rules, the layout,
and how to build and test. Read that first. This file explains how the parts
work together and gives recipes for common changes.

## Contents

- [How a request flows](#how-a-request-flows)
- [How the lock-down works](#how-the-lock-down-works)
- [Where state lives on disk](#where-state-lives-on-disk)
- [Linux and macOS](#linux-and-macos)
- [How to make common changes](#how-to-make-common-changes)
- [Known gaps and good first tasks](#known-gaps-and-good-first-tasks)

## How a request flows

1. The player types in the window. The UI (`app/packages/app`, inside the
   Electron renderer) sends it to the local server through the generated
   client.
2. The desktop main process started that server as a background service on
   `127.0.0.1`, port 29524 (29525 for an unpackaged run), with a password and
   with the environment from `SpecTunerEnv.make`.
3. The server runs a session with the `modder` agent. The session's folder is
   the game folder. The model request goes to the player's chosen provider and
   offers exactly the ten `game_*` tools.
4. The model calls a tool, for example `game_build_mod`. The plugin
   (`plugin/tools.ts`) adds the game folder from the session. The model never
   supplies a path. `plugin/sidecar.ts` starts the sidecar with the command
   name as argument and the JSON request on stdin.
5. The sidecar checks scope, takes a snapshot, changes only its own files in
   the game folder, starts the game when the command needs it, and writes one
   JSON envelope with a plain-language `summary` or `error.message`.
6. The plugin returns the envelope to the model, and `display.ts` gives the
   call a plain title for the conversation. The Changes panel is told to
   refresh.

The Changes panel does not go through the model. It calls the plugin's RPC
(`status`, `undo`, `restore`, `reapply`, `play`), which runs the sidecar's
`detect`, `changes`, `revert`, `reapply` and `play` directly.

## How the lock-down works

The layers are independent on purpose. Either of the first two alone leaves a
tool reachable; together they leave exactly the ten game tools.

1. **Config document.** `src/config.ts` builds a config passed to the server
   as `SPECTUNER_CONFIG_CONTENT`. It outranks every config file, so a stray
   file cannot undo it, while the player's provider settings still load. It
   makes `modder` the default agent with deny-all permissions plus one allow
   per `game_*` tool, gives the hidden `title`, `compaction` and `summary`
   agents no tools, disables the built-in agents, denies skills, removes the
   built-in tool plugins and the plugin that starts MCP servers from config,
   and turns off snapshots, sharing, automations and self-update.
2. **Server environment.** `src/env.ts` sets the server's folders, pins every
   experimental flag off, disables project config files, other agents'
   settings and free hosted models, and drops any inherited `SPECTUNER_*` or
   `XDG_*` setting that is not SpecTuner's own.
3. **Plugin.** `plugin/agents.ts` removes every agent except `modder` and the
   three hidden helpers, also when a config file adds one.
4. **Server refusal.** With `SPECTUNER_LOCKDOWN=1`, which `env.ts` always
   sets, `app/packages/server/src/middleware/lockdown.ts` answers 403 to every
   route family the app never uses: running commands, terminals, writing
   files, reading non-image files, tool servers, plugin installs, server
   config, remote access and pairing, web search, generation and voice. It
   runs before credentials are checked. `app/packages/server/test/lockdown.test.ts`
   lists every route left reachable, so a new route fails the test until
   someone decides about it.
5. **UI.** `app/packages/app/src/spectuner/profile.ts` hides coding commands,
   settings pages, shell mode and the agent picker. This is for clarity, not
   security; the layers above are what enforce the lock-down.
6. **Sidecar.** The scope gate refuses out-of-scope games, and the safety
   check refuses mod source that starts processes, uses the network, the
   registry, files, native calls or loads other code (`UNSAFE_CODE`).

## Where state lives on disk

| What | Where |
|---|---|
| Server data, config, cache and state (database `spectuner.db`, saved credentials, provider settings) | `%LOCALAPPDATA%\SpecTuner\app\` (`app-dev\` for an unpackaged run) |
| Window settings and saved window state | `%APPDATA%\com.spectuner.desktop\` (`com.spectuner.desktop.dev\` unpackaged) |
| Sidecar state per game: baseline manifest, change ledger, history, snapshots, mod sources, decompile cache, benchmark history | `%LOCALAPPDATA%\SpecTuner\games\<id>\`. `SPECTUNER_HOME` overrides `%LOCALAPPDATA%\SpecTuner`. Files are listed in the contract, section "State" |
| Unity's own log, read for mod errors | `Player.log` under `%USERPROFILE%\AppData\LocalLow\<company>\<product>\` (written by the game, never by SpecTuner). Linux: `~/.config/unity3d/<company>/<product>/`; macOS: `~/Library/Logs/<company>/<product>/`, both from Unity's manual and not yet seen on a real game |

The paths above are Windows. SpecTuner keeps one folder in each system's usual
place, and the server's home and the sidecar's state are inside it:

| System | SpecTuner's folder (`SPECTUNER_HOME`) | Window settings (Electron's userData) |
|---|---|---|
| Windows | `%LOCALAPPDATA%\SpecTuner` | `%APPDATA%\com.spectuner.desktop` |
| macOS | `~/Library/Application Support/SpecTuner` | `~/Library/Application Support/com.spectuner.desktop` |
| Linux | `${XDG_DATA_HOME:-~/.local/share}/SpecTuner` | `${XDG_CONFIG_HOME:-~/.config}/com.spectuner.desktop` |

`SpecTunerEnv.folder` in `app/packages/modder/src/env.ts` decides the folder,
and `SpecTunerEnv.make` always passes it to the server, and through it to the
sidecar, as `SPECTUNER_HOME`, so the two always agree. The sidecar falls back
to the same folders when it runs on its own. Development and packaged runs
share the sidecar's game state; the server's home is `app` or `app-dev`.

On macOS and Linux the desktop leaves its own `XDG_*` variables alone, because
the system reads them in the app (opening links in the browser, file dialogs).
It passes SpecTuner's `XDG_*` folders only to the server process it starts,
and finds the server's registration file by name
(`src/main/spectuner/server-environment.ts`, `spawnEnv` and
`registrationFile`). On Windows it works as before: the folders are set in the
app's own environment, which the server inherits.

The per-game id hashes the full game path, lower-cased on Windows and macOS
and as it is on Linux; on macOS the path is the folder that holds the `.app`.
On Linux and macOS a measured run also leaves `running.json` in the game's
state folder while it lasts (see "Starting a game" below).

Inside a game folder (on macOS, the folder that holds the `.app`, never the
app itself) the sidecar writes only these:

- the BepInEx loader: `BepInEx/` and the system's loader files at the root.
  Windows `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`; Linux
  `libdoorstop.so`, `run_bepinex.sh`, `.doorstop_version`; macOS
  `libdoorstop.dylib`, `run_bepinex.sh`, `.doorstop_version`;
- `BepInEx/plugins/SpecTuner/` (probe plugin and generated mods);
- `BepInEx/config/spectuner.*.json` (quality overrides, one-shot benchmark
  request);
- `BepInEx/spectuner/` (probe output);
- `BepInEx/config/BepInEx.cfg`, only in a BepInEx SpecTuner installed and only
  for Unity 6000 and later.

"Restore game" removes all of it.

## Linux and macOS

SpecTuner works with native Unity Mono games of the system it runs on:
Windows x64, Linux x64, and macOS on Apple Silicon (the sidecar also accepts
Intel Macs for games with an x86_64 build, but no Intel Mac app is built).
The contract's "Platforms" section is the reference; this section explains
how the code is arranged and what is still unproven.

**State of the work.** Everything below is written and covered by automated
tests. None of it has run on a real Linux PC or Mac with a real game. Treat
each Linux or macOS behaviour as unproven until a tester reports it (see
"What is unproven" below).

### Platform differences live in one place

Everything that differs per system is in
`sidecar/src/SpecTuner.Sidecar/Platform/`, behind one `PlatformProfile`:

| Part | What it decides |
|---|---|
| `IGameLayout` (`WindowsLayout`, `LinuxLayout`, `MacLayout`) | Where the program, the data folder, `Managed/` and the `UnityPlayer` library are; what the scope gate reads. `ExecutableHeader` reads ELF and Mach-O headers, including Mach-O slices and code signatures for `LOADER_BLOCKED` |
| `LoaderKit` | Which BepInEx package, which files it adds at the game root, which must be executable, how the game starts with and without the loader, and the Steam launch option |
| `UserFolders` | SpecTuner's default state folder and Unity's `Player.log` |
| `IGameProcesses` (`WindowsGameProcesses` with `JobObject`, `UnixGameProcesses`) | Whether the game is running, starting it, and closing it if the sidecar stops. Always the host's |
| `HostFiles`, `HostFacts` | Case sensitivity of the game folder's file system, file modes, facts for `diagnose` |

`Platforms.Current` returns the host's profile, or the one named by
`SPECTUNER_PLATFORM` (`windows`, `linux`, `macos`). Code outside `Platform/`
asks the profile and never checks the operating system itself. The switch
changes the layout, loader kit and folders, not the processes, so a Windows
machine can run the Linux and macOS install, restore and scope gate logic on
fake game folders. The app never sets the switch, and the server environment
drops it with every other inherited `SPECTUNER_*` setting.

### Starting a game

| System | With the loader | Without the loader (`play` with `withChanges: false`) |
|---|---|---|
| Windows | A plain start of `<Name>.exe`: the game loads `winhttp.dll` from its folder | `--doorstop-enabled false` (Doorstop's documented argument) |
| Linux | The program with the `DOORSTOP_*` variables, `LD_LIBRARY_PATH` and `LD_PRELOAD=libdoorstop.so`, as `run_bepinex.sh` sets them | A plain start |
| macOS | `/usr/bin/arch` with `ARCHPREFERENCE=arm64,x86_64` and the `DOORSTOP_*` variables, passing `DYLD_LIBRARY_PATH` and `DYLD_INSERT_LIBRARIES=libdoorstop.dylib` with `-e` to the program, because dyld strips `DYLD_*` variables from platform binaries such as `arch` | A plain start |

The sidecar reproduces what `run_bepinex.sh` would do; it never runs the
script and never edits it. The script is installed only so that a Steam
launch option can use it.

Measured runs (`launch-and-benchmark`, `build-plugin`'s test) must close the
game if the sidecar dies. Windows uses a job object with kill-on-close. Linux
and macOS have none: the sidecar closes the game on SIGTERM, SIGINT, SIGHUP,
SIGQUIT and a normal exit, and records the game in `running.json` so that the
next command that checks for a running game closes a game left behind by a
SIGKILL.

When a run leaves no BepInEx log, the loader never got into the game. The
failure data then has `loaderStarted: false`, and on Linux and macOS the
player's message says so; on macOS it adds that macOS may be keeping add-ons
out of the app. That is how a signature `LOADER_BLOCKED` could not see would
show up.

### The Play flow

1. The Changes panel (`app/packages/gui-plugins/src/spectuner-changes/`)
   shows **Play** when `detect` says the game is supported, and **Play without
   changes** when there is also at least one change in the list.
2. A click dispatches `play` in `machine.ts`; `panel.tsx` calls the plugin's
   RPC `play({ withChanges })` (`app/packages/modder/plugin/rpc.ts`).
3. `plugin/changes.ts` runs the sidecar's `play` with a one-minute timeout.
4. The sidecar checks scope, takes the lock while it starts the game, deletes
   a leftover benchmark request, starts the game detached (no shared handle or
   pipe) and returns at once. It does not wait, measure or revert.
5. The panel shows the summary and the `note`. On Linux and macOS, when the
   loader is in place and the game folder is inside `steamapps/common`, it also
   shows `steamLaunchOption` with a **Copy** button: Linux
   `./run_bepinex.sh %command%`, macOS `"<game folder>/run_bepinex.sh" %command%`.

The modder agent has no play tool; its prompt tells the player to press
**Play**.

### Diagnose

`diagnose` is a read-only report for testers' bug reports: system, state
folder, game folder facts, executable headers and signatures, loader files,
log summaries, SpecTuner's record. It runs without the scope gate, writes
nothing, and replaces paths, the user name and the computer name with
placeholders. It is run from the installed app:

| System | The sidecar inside the installed app |
|---|---|
| macOS | `/Applications/SpecTuner.app/Contents/Resources/spectuner/spectuner-sidecar` |
| Linux, deb | `/opt/SpecTuner/resources/spectuner/spectuner-sidecar` (electron-builder's default install folder; `dpkg -L spectuner` confirms it) |
| Linux, AppImage | `resources/spectuner/spectuner-sidecar` inside the image, reached with `--appimage-extract` |
| Windows | `resources\spectuner\spectuner-sidecar.exe` in the install folder |

### Packaging and data folders

`build.sh` builds the system it runs on: Windows the NSIS installer (with
`--installer`), Linux x64 an AppImage and a deb, macOS on Apple Silicon a dmg
and a zip. electron-builder signs the whole macOS bundle ad hoc (the server
and sidecar inside it included), without the hardened runtime, which would
make macOS reject Electron's own frameworks with an ad hoc signature. The
macOS app declares plain-language reasons for the folder prompts
(`MAC_FOLDER_PROMPTS` in `electron-builder.config.ts`). `scripts/check-build.sh`
checks a build on its own system; the smoke test runs it with
`--start-app`.

### What is unproven

Nobody has checked these on a real machine. Each needs a tester's report (or
a test that really runs it) before anyone says it works.

- The loader really loading when SpecTuner starts a game on Linux, and on
  Apple Silicon, without BepInEx's script.
- Starts from Steam with the launch option; Steam's runtime container for
  native Linux games; Flatpak Steam (`diagnose` reports `flatpakSteam`).
- macOS: whether games signed with the hardened runtime really refuse the
  loader, and whether `LOADER_BLOCKED` catches exactly those. The rule comes
  from Apple's documentation, not from a real game. There is an open upstream
  report that the loader fails on macOS games built with the newest Unity.
- macOS: opening the unsigned app with "Open Anyway", the folder prompts,
  the background server.
- Linux: the AppImage on Ubuntu 24.04 (libfuse2; Electron's sandbox under
  AppArmor), the deb's launcher and icon, sign-in links opening the browser,
  the system keyring.
- Where Unity writes `Player.log` on Linux and macOS (the sidecar follows
  Unity's manual), and whether the Unity 6000 entrypoint workaround
  (`BepInEx.cfg`) is needed there; it is applied on every system today.

## How to make common changes

### Add or change a sidecar command

1. Add a class in `sidecar/src/SpecTuner.Sidecar/Commands/` that implements
   `ICommand` (`Name` and `Execute`). Start with
   `GameCommands.RequireInScope(request)` so the scope gate runs. A command
   that changes the game gets the loader through `GameSetup.EnsureInstalled`
   and wraps its file changes in `Ledger.Begin`, then `Commit` or `Abandon`;
   `Begin` takes the snapshot. Follow `SetQualityCommand`.
2. Register it in `Cli/CommandHost.cs`. Add new error codes to
   `Cli/ErrorCodes.cs`.
3. Write `summary` and `error.message` for a player: one plain sentence, no
   paths, no code. Keep the output small; cap lists and set `truncated`.
4. Document the request, the data and the error codes in
   `docs/sidecar-contract.md`. The contract is the interface the TypeScript
   side builds against.
5. Add tests in `sidecar/tests/SpecTuner.Sidecar.Tests/` with the fake game
   fixtures in `Support/`. Tests set `SPECTUNER_HOME` to a temp folder. If
   the command touches the game folder or starts the game, test it for each
   system: `CreateGame(GamePlatform.Linux)` or `GamePlatform.MacOS` with
   `UsePlatform`.
6. Run `dotnet test SpecTuner.Sidecar.slnx`, then try it on a copy of a real
   game:

   ```sh
   # in the repository root, after ./sidecar/publish.sh
   echo '{"gamePath":"C:/Games/Sample Game"}' | artifacts/sidecar/spectuner-sidecar.exe detect
   # on Linux, after ./sidecar/publish.sh linux-x64
   echo '{"gamePath":"/home/player/Games/Sample Game"}' | artifacts/sidecar/linux-x64/spectuner-sidecar detect
   ```

### Change something that differs per system

1. Put the difference in `sidecar/src/SpecTuner.Sidecar/Platform/`: a member
   on `IGameLayout`, `LoaderKit`, `UserFolders` or `IGameProcesses`, with one
   implementation per system. Do not test `OperatingSystem.Is...` outside that
   folder; only code in `Platform/` asks the real system.
2. Keep Windows behaviour exactly as it was unless the change is meant for
   Windows too.
3. Test each system through `SPECTUNER_PLATFORM` (`UsePlatform`). A test that
   needs a real process, signal or file mode carries `[OSCondition]` with the
   reason; it runs only when the suite is run on that system.
4. Update the contract ("Platforms", the scope gate, the command).
5. Add the new behaviour to "What is unproven" above until a tester has
   checked it on a real machine.

### Expose a sidecar command to the agent as a tool

1. `app/packages/modder/plugin/tools.ts`: add the name to `names` and a
   definition in `make`, with an input schema (the model never passes
   `gamePath`; the plugin adds it), a description written for the model, and
   a timeout. Inputs not in the schema are dropped. Do not expose knobs the
   model should not turn (measurement design, the build's test run).
2. `app/packages/modder/src/config.ts`: add the name to `TOOLS`, the
   allowed-tool list. `test/sidecar.test.ts` fails if the two lists differ,
   and `test/config.test.ts` spells out the expected allow rules.
3. `app/packages/modder/plugin/display.ts`: add a plain title for the
   conversation. A test fails for any tool without one.
4. Tell the agent when to use it in `src/prompt/modder.txt`.
5. Tests: `test/display.test.ts` (title), `test/config.test.ts` and
   `test/sidecar.test.ts` (tool list and inputs), `test/lockdown.test.ts`
   (a scripted call of every tool), and the fake
   sidecar in `test/fixture/fake-sidecar.ts` if the tool needs a canned
   answer.
6. Run `bun run test` and `bun typecheck` in `app/packages/modder`, then
   `bun run script/tools.ts --game <folder>` to see the real tool list.

The plugin folder ships as source and is loaded by path, so files in
`plugin/` may import only Node built-ins, `effect`, `@spectuner/plugin` and
each other.

### Change what the agent is told

The prompt is `app/packages/modder/src/prompt/modder.txt`. The build stages it
into the app as `modder.txt`. Tool descriptions in `plugin/tools.ts` are part
of what the model reads too.

To check the effect, run the modder tests, then a real conversation on a copy
of a game with `script/e2e.ts`, several times, with the kind of model players
use (small local models behave differently from large hosted ones). Read the
transcript: did it call `game_detect` first, measure before and after, keep
only measured gains, use the right mod shape, and avoid code in its replies?

### Change the Changes panel

- Look and behaviour: `app/packages/gui-plugins/src/spectuner-changes/`
  (`panel.tsx` view, `machine.ts` state, `renderer.tsx` registration,
  `i18n/en.ts` text). Tests in `machine.test.ts`.
- Data: the panel calls the plugin's RPC, defined in
  `app/packages/modder/plugin/rpc.ts` and served by `plugin/changes.ts` from
  the sidecar's `detect`, `changes`, `revert`, `reapply` and `play`. Tests in
  `app/packages/modder/test/changes.test.ts`.
- Play: the buttons, the note and the Steam launch option with **Copy** are
  in `panel.tsx` (`SteamLaunchOption`); when they show is decided in
  `machine.ts` (`canPlay`, `canPlayWithout`). See "The Play flow" above.
- If the panel needs new data from the sidecar, change the sidecar command and
  the contract first.
- Run `bun run test` in `gui-plugins` and `modder`, the typecheck, and look at
  it in the unpackaged app.

### Change what the server refuses

1. Edit `RULES` in `app/packages/server/src/middleware/lockdown.ts`. Rules
   match the route template and method, never the raw URL.
2. Update `ALLOWED` in `app/packages/server/test/lockdown.test.ts`. Every
   route must be either refused or listed there; the coverage test fails
   otherwise. A new route in the server fails this test until you decide.
3. Before refusing a route, check that the app does not call it. A refused
   read can break a screen without an obvious error.
4. Run `bun run test` in `app/packages/server`, and the modder suite
   (`test/lockdown.test.ts` drives a whole server).

### Change the icon

Follow `branding/README.md`. `make_icon.py` builds every size with Blender
(`--all`) and the macOS icon (`--mac`); `verify_ico.ts` checks `icon.ico` and
`verify_icns.ts` checks `icon.icns`. Then copy the files to where the app uses
them, all listed in that README: `app/packages/desktop/icons/{dev,beta,prod}/`
(`icon.ico`, `icon.icns`, the Linux `<size>x<size>.png` set and `icon.png`),
the notification icon in `app/packages/desktop/src/renderer/platform/`, the
favicons in `app/packages/app/public` and `app/packages/ui/src/assets/favicon`,
and the logo marks in `app/packages/ui/src/assets/logo/`.

### Change the first-run or settings screens

| What | File |
|---|---|
| Home's first steps (connect a model, choose the game's folder) | `app/packages/app/src/spectuner/home-start.tsx` |
| Choosing a game folder opens a chat on it | `app/packages/app/src/spectuner/choose-game.ts` |
| Local model form | `app/packages/app/src/spectuner/local-model.tsx`, `local-model-machine.ts`; server side in `app/packages/modder/plugin/local-model.ts` |
| Which settings pages, rows, commands and menu entries a player gets | `app/packages/app/src/spectuner/profile.ts` (`hiddenSettingsPages`, `hiddenSettingsRows`, `hiddenCommands`), `settings-view.ts` |
| About page | `app/packages/app/src/spectuner/about.tsx` |
| Wording that replaces the starting code base's text | `app/packages/app/src/spectuner/i18n.ts` |
| Text of SpecTuner's own screens | the `spectuner.*` keys in `app/packages/app/src/runtime/i18n/en.ts` |
| First launch in the desktop shell | `app/packages/desktop/src/renderer/onboarding/index.tsx` |

Run the SpecTuner UI tests and the app typecheck (commands above), then look
at the screens in the unpackaged app.

## Known gaps and good first tasks

### Known gaps

- **IL2CPP games are not supported.** They are detected and declined.
- **Little real-game coverage.** End-to-end work has been done on very few
  games. The anti-cheat and networking checks are pattern tables
  (`sidecar/src/SpecTuner.Sidecar/Games/Signatures.cs`) and will miss some.
- **Benchmarks measure what the game shows after start-up**, often a title
  screen, not gameplay. SpecTuner cannot press keys, so a new behaviour mod is
  confirmed to load without errors, not confirmed by playing.
- **Small local models** may loop or fail on mod requests. The prompt and the
  build's test run reduce this; they do not remove it.
- **Coding features from the starting code base are still in the tree**: the
  coding tools and services in `app/packages/core`, and screens the SpecTuner
  profile hides in `app/packages/app`. They are switched off by configuration
  and refused by the server, not removed.
- **No automatic updates** and no link registration. Players install new
  versions by hand.
- **English only.** The translation mechanism remains; no other language
  ships.
- **Linux and macOS are untested on real machines.** The builds and the
  sidecar's support for native Linux and macOS Unity Mono games are written
  and covered by automated tests, which run on Windows through
  `SPECTUNER_PLATFORM`. The smoke test builds and starts the app on GitHub's
  Linux and macOS runners. No real game has been tried on either system; the
  open points are under "What is unproven" above. The macOS app has only an ad hoc signature, so players must
  use "Open Anyway".
- **On Linux and macOS the loader does not start by itself.** A start from
  Steam needs the launch option the Changes panel shows; a start from
  anywhere else outside SpecTuner runs the game without its changes.
- **Windows games through Proton or Wine are declined**, as are macOS games
  whose signature keeps the loader out (`LOADER_BLOCKED`).
- **Builds are not code-signed.**
- **The background server outlives the window.** It listens only on
  localhost, behind a password.
- **Upgrade from builds made before the rename** does not carry over theme
  and notification choices, or sign-ins of two built-in providers whose
  storage keys changed.
- **No support channel** is linked from the app.

### Good first tasks

- Some source comments still say "upstream" or "the fork" when they mean the
  code SpecTuner started from. Reword them for an independent code base.
- Several per-package `AGENTS.md` files still describe the starting code
  base's workflows. Trim them to what applies to SpecTuner.
- Stop the background server when the player quits the app.
- Remove the unused coding tools and hidden screens (a larger refactor of
  `app/packages/core` and `app/packages/app`; keep the tests of what remains).
