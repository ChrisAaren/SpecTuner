# Working on SpecTuner in depth

This goes with [CLAUDE.md](../CLAUDE.md), which holds the rules, the layout,
and how to build and test. Read that first. This file explains how the parts
work together and gives recipes for common changes.

## Contents

- [How a request flows](#how-a-request-flows)
- [How the lock-down works](#how-the-lock-down-works)
- [Where state lives on disk](#where-state-lives-on-disk)
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
(`status`, `undo`, `restore`, `reapply`), which runs the sidecar's `detect`,
`changes`, `revert` and `reapply` directly.

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
| Unity's own log, read for mod errors | `Player.log` under `%USERPROFILE%\AppData\LocalLow\<company>\<product>\` (written by the game, never by SpecTuner) |

Inside a game folder the sidecar writes only these:

- the BepInEx loader: `BepInEx/`, `winhttp.dll`, `doorstop_config.ini`,
  `.doorstop_version`;
- `BepInEx/plugins/SpecTuner/` (probe plugin and generated mods);
- `BepInEx/config/spectuner.*.json` (quality overrides, one-shot benchmark
  request);
- `BepInEx/spectuner/` (probe output);
- `BepInEx/config/BepInEx.cfg`, only in a BepInEx SpecTuner installed and only
  for Unity 6000 and later.

"Restore game" removes all of it.

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
   fixtures in `Support/`. Tests set `SPECTUNER_HOME` to a temp folder.
6. Run `dotnet test SpecTuner.Sidecar.slnx`, then try it on a copy of a real
   game:

   ```sh
   # in the repository root, after ./sidecar/publish.sh
   echo '{"gamePath":"C:/Games/SampleGame"}' | artifacts/sidecar/spectuner-sidecar.exe detect
   ```

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
  the sidecar's `changes`, `revert` and `reapply`. Tests in
  `app/packages/modder/test/changes.test.ts`.
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

Follow `branding/README.md`. `make_icon.py` builds every size with Blender;
`verify_ico.ts` checks `icon.ico`. Then copy the files to where the app uses
them, all listed in that README: `app/packages/desktop/icons/{dev,beta,prod}/icon.ico`,
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
- **Windows x64 only.** Mac and Linux packaging was removed.
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
