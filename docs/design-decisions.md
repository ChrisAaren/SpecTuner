# Design decisions

Why the code is the way it is. Each entry states the decision, then the
reason, then what was considered and rejected where that is known. The rules
these decisions serve are in [CLAUDE.md](../CLAUDE.md); the sidecar's
interface is in [sidecar-contract.md](sidecar-contract.md).

## Contents

- [Scope](#scope)
- [Game files and state](#game-files-and-state)
- [The mod loader and the probe](#the-mod-loader-and-the-probe)
- [Measuring performance](#measuring-performance)
- [Building and checking mods](#building-and-checking-mods)
- [The modder agent](#the-modder-agent)
- [Local models](#local-models)
- [Lock-down](#lock-down)
- [App identity and isolation](#app-identity-and-isolation)
- [Independence from opencode](#independence-from-opencode)
- [Upgrading from builds made before the rename](#upgrading-from-builds-made-before-the-rename)
- [Language](#language)
- [Testing](#testing)
- [Icon](#icon)

## Scope

### The sidecar decides what is in scope

Every sidecar command except `detect` and `diagnose` runs the same scope gate
first and refuses with one error code when the game is out of scope: not
found, a Windows game meant for Proton or Wine, not Unity, IL2CPP, the wrong
architecture, anti-cheat, protected code, a macOS app that keeps the loader
out, or online multiplayer. The pattern tables are in
`sidecar/src/SpecTuner.Sidecar/Games/Signatures.cs`; the order and details of
the checks are in the contract.

**Why.** One place decides, so the agent, the Changes panel and any future
caller all decline the same games with the same plain explanation. The
sidecar only detects these things and never works around them.

**Exception.** `changes` and `revert` still run for a game that has fallen out of
scope (for example a game update added a networking library) when SpecTuner
already has state for it. The player can always see and undo what SpecTuner
did.

Unity sign-in, cloud save and analytics libraries and Steamworks.NET do not
count as online multiplayer, because many single-player games ship them.

### Native Linux and macOS games are in scope, untested by players

SpecTuner started as Windows x64 only. The scope now also covers native Unity
Mono games on Linux x64 and on macOS with Apple Silicon. Windows remains the
finished system, tested with a real game; Linux and macOS support is written and
covered by automated tests, and stays labelled untested until testers report
on real machines.

**Why.** Many Unity games ship native Linux and macOS versions, and BepInEx 5
publishes official packages for both, so the same design (an unmodified
loader next to the game, mods compiled against the game's own assemblies)
carries over. What differs is small enough to isolate (see "Platform
differences live in one place"). **Cost.** The development machine runs only
Windows, so nothing Linux- or macOS-specific can be checked there on a real
game. Every claim about those systems needs a test or a tester's report.

### Windows games through Proton or Wine are declined

On Linux and macOS, a folder with a Windows game (`.exe` with a `_Data`
folder) and no native game gets `COMPATIBILITY_LAYER_NOT_SUPPORTED`.

**Why.** SpecTuner works with games native to the system it runs on. A game
inside a compatibility layer is started by that layer, not by SpecTuner, so
neither the Windows way of loading BepInEx (the game finds `winhttp.dll`) nor
the Linux and macOS way (SpecTuner starts the game with the loader's
environment) is known to apply, and nothing here can test it. A clear refusal
is better than a change that may silently do nothing. It can be revisited
once the native path is proven.

### IL2CPP is deferred

IL2CPP games are declined today with `IL2CPP_NOT_SUPPORTED`. The design keeps
room for them. The first step would be settings-only tuning through the probe,
with code mods later through Il2CppInterop and Cpp2IL.

## Game files and state

### State lives outside the game folder

Snapshots, the baseline manifest, the change ledger, history, mod sources and
the decompile cache live under `%LOCALAPPDATA%\SpecTuner\games\<id>\` (macOS
`~/Library/Application Support/SpecTuner`, Linux
`${XDG_DATA_HOME:-~/.local/share}/SpecTuner`).
Inside the game folder SpecTuner writes only BepInEx and its own files under
it.

**Why.** "Restore game" stays simple (remove what we added, compare against the
baseline), and the game folder stays clean. A game update or a store's file
check never meets SpecTuner's bookkeeping.

### "Restore game" removes only what SpecTuner added

Files that appeared in the game folder after the baseline and are not
SpecTuner's, such as a save file a game writes next to its exe, are left in
place and reported, not deleted.

**Why.** Deleting them would risk a player's saves.

### Past changes are kept and can be applied again

The sidecar keeps every change it has made in `history.json` under its state
folder, so the record survives "Restore game" while the game folder is still
left exactly as found. Mod sources are kept for the same reason. `reapply`
puts a past quality change or mod back as a new change, through the same code
as `set-quality` and `build-plugin`, including the safety check and the test
start for mods. A change that never worked is listed but cannot be put back,
with a plain sentence saying why. The agent has the same ability through
`game_reapply`, and the Changes panel through "Apply again".

### Saved settings are enforced while the game runs

The probe re-applies a saved quality override if the game's own settings code
changes it back.

**Why.** Many games apply their own settings after start-up, which would undo
the change silently. **Consequence.** A saved override wins over the in-game
settings menu for that key until the player undoes the change.

## The mod loader and the probe

### BepInEx 5, bundled unmodified

SpecTuner bundles BepInEx 5.4.23.6 as the unmodified upstream zips for
Windows x64, Linux x64 and macOS (universal), all three embedded in every
sidecar build, checked against their SHA-256 and installed per game. The
hashes are recorded in `THIRD_PARTY_NOTICES.md`. `BepInEx/core` is the same in
all three; only the Doorstop library and the start script differ. Its `changelog.txt` is never extracted,
so it cannot collide with a game's own file.

**Why.** BepInEx 5 is the established Mono mod loader and ships HarmonyX for
runtime patching. The licence was checked against the release tags, not the
default branches: BepInEx 5.x is MIT (the 6.x line is LGPL-2.1), and the
UnityDoorstop 4.6.0 it bundles (`winhttp.dll`) is LGPL-2.1, shipped as the
unmodified upstream binary with its licence text.

If a game already has the player's own BepInEx 5, SpecTuner uses it and never
removes it. Another mod loader, or BepInEx 6, is refused.

### On Linux and macOS SpecTuner starts the game itself, and there is a Play button

On Windows the game loads Doorstop's `winhttp.dll` from its own folder, so
the loader starts with every launch from anywhere. On Linux and macOS nothing
loads a library from the game folder by itself: Doorstop has to be injected
through environment variables when the game starts. BepInEx's own answer is
`run_bepinex.sh`, which players normally edit and run. SpecTuner instead
starts the game with the same variables the script would set (Linux
`LD_PRELOAD`; macOS `DYLD_INSERT_LIBRARIES` passed through `/usr/bin/arch`),
for its measured runs, its mod tests and the new `play` command. The Changes
panel shows **Play** and **Play without changes**. For Steam games, `play`
returns the launch option BepInEx documents (`./run_bepinex.sh %command%`),
and the panel shows it with a **Copy** button.

**Why.** Editing a shell script is not something a non-technical player
should have to do, and a script edited by hand cannot be undone cleanly. The
sidecar copies the script's defaults, never runs or edits it, and installs it
only for the Steam launch option. **Play without changes** gives players a
way to compare, and to tell whether a problem comes from a change; on
Windows it uses Doorstop's documented `--doorstop-enabled false`, so no file
changes there either. The launch option is shown, not written into Steam's
settings: SpecTuner changes only its own files.

### A macOS app is never altered or re-signed

On macOS everything SpecTuner adds sits next to the `.app`, never inside it.
If the program slice that would run is signed with the hardened runtime and
lacks the entitlements that allow injected libraries
(`com.apple.security.cs.allow-dyld-environment-variables` or
`com.apple.security.cs.disable-library-validation`), the game is declined
with `LOADER_BLOCKED`.

**Why.** Writing into the app, or stripping and replacing its signature,
changes the player's game files and breaks the developer's seal, which the
game-files rules forbid. It would also mean working around a macOS
protection, which SpecTuner never does. Declining clearly is the honest result. **Unproven.**
The rule is a reading of Apple's documentation; no real game has confirmed
it. A run where the loader never started adds a plain sentence that macOS may
be keeping add-ons out of the app, which covers cases the rule misses.

### BepInEx entrypoint for Unity 6000 and later

With BepInEx 5.4.23.6's default entrypoint, Unity 6000.6 crashes inside
`GameObject.AddComponent` as soon as any plugin loads. For Unity 6000+ games,
and only in a BepInEx SpecTuner installed itself, the sidecar writes
`[Preloader.Entrypoint] Type = Camera` (a documented BepInEx option) into
`BepInEx/config/BepInEx.cfg`. The file is part of the loader change and is
removed by "Restore game".

### The probe plugin is compiled on the player's machine

The probe's C# source is embedded in the sidecar and compiled at install time
with Roslyn against the game's own `Managed/` assemblies and BepInEx's core
assemblies, the same path mods use. The language version is C# 9, the newest
Unity's own compiler supports.

**Why.** SpecTuner ships no Unity assemblies, which it may not redistribute,
and the probe always matches the game's Unity version.

## Measuring performance

### What is measured

The game is started with no extra arguments and measured on whatever it shows
after start-up, often a title screen with the scene rendered behind it, not
gameplay. During measurement the frame cap and vsync are lifted and the game
keeps rendering in the background, so runs are comparable. These three are
never saved as settings.

The game's own benchmark mode, where one exists, is not used: SpecTuner must
work on games that have none, and such modes may write into the game folder.

### Changes are judged inside one launch

Launch-to-launch noise in average FPS was several percent and up to about 10
percent, and far more for 1% lows. A difference of two separate launches is
therefore not evidence. A candidate setting is tested inside one launch, in
alternating windows with and without it: by default 4 pairs of 5-second
windows with a 2-second settle pause.

The verdict (`sidecar/src/SpecTuner.Sidecar/Benchmark/Verdict.cs`):

- `better`: the mean paired change in average FPS is at least +3 percent and
  every pair is positive, allowing one exception when there are more than 3
  pairs;
- `worse`: the mirror image;
- `not_applied`: none of the tried settings took effect;
- otherwise `no_clear_difference`.

Separate launches that differ by less than 10 percent are reported as normal
run-to-run variation (`withinNoise`).

### The model cannot change the measurement design

`game_benchmark` does not accept the number of pairs, window length or settle
time; the sidecar's defaults always apply.

**Why.** In a trial run a model set 2 pairs on every trial, which made a real
gain look inconsistent. The measurement design belongs to the sidecar.

## Building and checking mods

### Mods are compiled on the player's machine

Mods are C# 9 BepInEx 5 plugins compiled in-process with Roslyn against the
game's own `Managed/` assemblies plus BepInEx's core assemblies.

**Why.** The same as for the probe. Nothing from Unity is shipped, and the mod
matches the exact game version.

### Generated mods are checked before they are built

Source that starts processes, uses the network, the registry, files, threads,
timers, native calls, `dynamic`, or loads or generates other code is refused
with `UNSAFE_CODE`. The check works on resolved symbols from Roslyn's semantic
model, so aliases and fully qualified names are caught.

**Why.** A mod only needs to patch the game. The check limits the damage if a
model is tricked into writing something else. **Known limit.** Code reached only
through reflection strings is not caught, because mods need reflection to read
game fields.

### The safety check covers everything a mod can reference

Every dll in the game's `Managed/` folder, plus BepInEx's core, is a compile
reference, so a mod can call anything in them, not only the .NET basics. A
review of `BepInEx/core`, the UnityEngine modules and the Mono libraries games
ship added about a hundred rules to `SafetyCheck.Rules`: native code through
MonoMod (`DynDll`, `NativeDetour` and the like) and `BepInEx.Unix`, direct
system calls through `Mono.Unix` and `Mono.Posix`, generating or loading code
(MonoMod's `DynamicMethodDefinition`, `Mono.CSharp`, BepInEx's preloader and
type loader), file access through XML, zip, Cecil, SQLite, resources, Harmony's
file log, BepInEx config files at any path, screenshots and asset bundles,
the legacy networking classes, forced crashes, more thread APIs, and camera
and microphone. The contract lists them all.

**Why.** A rule on `File` or `DllImport` means little if another referenced
library offers the same reach. `Mono.Posix` and MonoMod's Unix code, for
example, give direct system calls (chmod, unlink, exec, setenv), which
matters more now that Linux and macOS games are in scope. Allowed as before:
reflection, `Path`, `MemoryStream`, the plugin's own `Config`, and all of
HarmonyLib's patching.

### Every mod build is tested

`build-plugin` starts the game once after every build (a short warm-up and a
few seconds running, not a benchmark). A game that fails to start reverts the
recent changes, as for any change. A mod that did not load, or that logged
errors, is removed again and the errors go back to the model as
`MOD_FAILED_TEST`. The tool does not expose the switch, so the model cannot
skip the test.

**Why.** Without it, a model built a mod, skipped the separate test launch and
told the player it worked. The cost is one game start per build.

### Mod errors are read from Unity's Player.log too

BepInEx 5 keeps Unity's own messages out of `LogOutput.log` by default, so an
exception thrown in a mod's `Awake` or `Update` appears only in `Player.log`
under `%USERPROFILE%\AppData\LocalLow` (Linux `~/.config/unity3d`, macOS
`~/Library/Logs`, as Unity's manual says; not yet confirmed on a real game).
The sidecar reads it when it was written during the run. That file is shared by every copy of the same game on
the PC.

**Considered and rejected.** Setting BepInEx's `WriteUnityLog = true`, which would
not help when the player already had their own BepInEx.

## The modder agent

### The tools are one server plugin, and the model never supplies a path

The ten `game_*` tools live in one server plugin,
`app/packages/modder/plugin/`. Every request to the sidecar takes the game
folder from the session's folder.

**Why.** A model that cannot name a path cannot point the sidecar at another
folder. One plugin also means one place that defines what the agent can do.

### Tool output stays small

The sidecar returns search hits with signatures, outlines of long types,
capped lists and one-sentence summaries, never whole decompiled files.

**Why.** Small and local models have small context windows, and a flood of
code makes every model worse at the task.

### The prompt separates value changes from new behaviours

The prompt (`app/packages/modder/src/prompt/modder.txt`) gives two shapes for
a mod: set a value once per object, or run every frame. It says that "pressed
this frame" checks are true for one frame only, and that the mod should change
the fields the game's code already uses instead of moving objects with
separate code.

**Why.** An earlier prompt had one example, a patch that runs once per object.
A model copied it for "let me fly", so the mod checked the key on the first
frame only. SpecTuner cannot press keys, so for a new behaviour the agent must
say that the mod loaded and that the player has to try it.

### Private by default

OpenCode's free hosted models are switched off, so a first request cannot go
to a hosted model the player did not choose. Skills found in the player's home
folder, and instruction or config files inside a game folder, are kept out of
the modder's context.

## Local models

### Context window detection for llama.cpp's router

The local model form asks the server, not the window, for the endpoint's
models, so browser cross-origin rules do not apply. llama.cpp's router reports
`n_ctx: 0` on `/props`, and falling back to a default window of 8,192 tokens
made sessions with larger models fail with "Compaction summary reached the
output token limit". The form therefore reads `--ctx-size` (or `-c`) from each
model's `status.args` in `/v1/models`.

It does not call `/props?model=`, because that loads the model and evicts the
one in use. The output limit is a quarter of the window, at most 8,192 tokens:
it also caps the compaction summary, and a model that starts repeating itself
runs until it. The form warns when the window is under 16,000 tokens.

## Lock-down

### Layers, with the config passed inline

The lock-down combines deny-all permission rules plus one allow per `game_*`
tool, removal of the built-in tool plugins, a plugin that removes every other
agent, and the server refusals below. The config document is passed as
`SPECTUNER_CONFIG_CONTENT`.

**Why.** Either of the first two alone leaks a tool (`skill`, or Code Mode's
`execute`); together they leave exactly the ten game tools. The inline
document outranks every config file, so a stray file cannot undo it, while the
player's own provider settings in the writable config folder still load. The
hidden `title`, `compaction` and `summary` agents stay because the server
needs them; they get no tools.

### The server refuses what SpecTuner never needs

The server started as a general coding server. With `SPECTUNER_LOCKDOWN=1`,
always set by SpecTuner's server environment, it answers 403 to every route
family the app does not use: running commands, terminals, writing files and
reading non-image files, adding tool servers or plugins, editing config
through the API, remote access and pairing, web search, generation and voice.
Tool servers named in a config file are not started. Developer tools are
switched off in packaged builds.

A deny-list was chosen over an allow-list. **Why.** The app's reads are spread
through a generated client, and one missed read would break a screen
silently. To keep the deny-list honest, a test lists every route left
reachable, so a new route fails until someone decides about it.

The UI also hides every entry point to these features, but the server refusal
is what enforces it: a local program could otherwise call the endpoints
directly.

## App identity and isolation

### Own identity, folders and port

App id `com.spectuner.desktop`, product name SpecTuner, server data under
`%LOCALAPPDATA%\SpecTuner\app`, service port 29524 (29525 and separate folders
for unpackaged runs). There is no link-protocol registration and no
auto-updater, because both pointed at upstream services. An installed OpenCode
on the same PC is never adopted, stopped or shared with.

**Why.** SpecTuner must never meet another app's background service, database,
config or port, and a development build must be able to run next to an
installed one.

### The background server keeps running after the window closes

This is the starting code base's behaviour: the service is detached. Stopping
it on quit would mean changing the service lifecycle code, so it was left as
is. It listens only on localhost, behind a password.

## Independence from opencode

### SpecTuner is its own application

SpecTuner's app code started from opencode 2.3.0 (MIT), commit `dcb24c74dc`.
It is now an independent code base: there is no merging from opencode, and
names inside the code say SpecTuner (package scope `@spectuner/*`,
`SPECTUNER_*` settings, `spectuner.db`, `spectuner.jsonc`, the
`spectuner-server` program). opencode's licence text stays in `app/LICENSE`.

**Why.** Keeping the code mergeable cost more than it saved once SpecTuner's
needs diverged.

### What still says opencode, on purpose

- Third-party packages: `@opencode/pty` and its platform packages, and other
  npm packages with opencode in their name.
- OpenCode's own model services ("OpenCode Go", "OpenCode Console",
  "OpenCode Zen"), their ids in the model catalog, and whatever is sent to
  them or to other model providers (headers, user agent).
- Web addresses.
- Recorded test data, snapshots, database migrations and the upstream
  changelog, which are identified by their content.
- The licence and the "started from opencode" credit.
- The old names the upgrade step looks for.

### OpenCode's services are ordinary providers

"OpenCode Go" and "OpenCode Console" stay in the model list under their real
names. They do not come first, carry a "Recommended" badge or a tagline. The
"Popular" group holds Anthropic, OpenAI, Google and OpenRouter. Links to
opencode's documentation, Discord and GitHub were removed from every screen,
and the crash screen does not offer to report to opencode's community.

### What was removed, and what was not

**Removed.** Unused workspace packages, opencode's web services, SDKs, specs, CI,
editor and repository tooling, translated readmes and upstream docs; the
built-in GUI plugins SpecTuner never showed (terminal, file browser, code
review, browser pane and others); the terminal UI and every command of the
server program except `serve` and `--version`; the other languages; Mac and
Linux packaging (since rebuilt for SpecTuner, see "Linux and macOS builds"), and
the self-updater.

**Kept.** The tests of the remaining code, because they are how a change is
checked. Also still in the tree although unused: the coding tools and coding
services inside the core package (switched off by configuration and refused
by the server) and app screens the SpecTuner profile hides. Removing them is a
refactor of the core and has not been done.

### Linux and macOS builds

The app builds for Linux x64 (AppImage and deb) and macOS on Apple Silicon
(dmg and zip) as well as Windows x64, with the server and sidecar bundled.
A smoke test on GitHub's Linux and macOS runners builds them and starts the
packaged app; nobody has used them on a real Linux PC or Mac yet.

- **Built on each system.** electron-builder refuses to build macOS apps on
  Windows, and Linux packages are built on Linux, so each system builds its
  own sidecar, server program and app, on a tester's machine or in the smoke
  test's job for that system. The server program is compiled natively rather
  than cross-compiled.
- **Unsigned macOS builds.** The macOS app gets an ad hoc signature over the
  whole bundle, the server and sidecar inside it included, without the
  hardened runtime. **Why:** Apple Silicon refuses to run arm64 code with no
  signature at all, and an ad hoc signature keeps the seal valid so a player
  can open the app with "Open Anyway". The hardened runtime with an ad hoc
  signature would make macOS reject Electron's own frameworks. **Rejected for
  now:** an Apple Developer ID (paid) for signing and notarization. Windows
  builds are not code-signed either.
- **One workflow: a smoke test.** `smoke.yml` builds SpecTuner on GitHub's
  standard Linux, macOS and Windows runners and starts the packaged app.
  There is no other workflow and no other job. **Why:** the runners exist to
  answer one question: does the app build and start on each system. A
  development machine that runs only Windows cannot answer it for Linux and
  macOS.
- **Only the owner starts it, and it hands out nothing.** Every job is
  skipped unless the repository's owner started the run. Nothing runs for a
  pull request or an ordinary push. A run keeps no build to download, and no
  workflow publishes a release: releases are built and published by hand, and
  testers build from their own clone. **Why:** who uses the runners, and who
  gets a build and when, stays the owner's decision.
- **It runs only while the repository is public.** Every job is skipped
  unless the repository is public, has a time limit, and is cancelled by a
  newer run of the same branch. **Why:** under those conditions the jobs cost
  nothing; if the repository is made private, they stop instead of running up
  a bill.
- **Same lock-down.** The server ignores machine-wide config and macOS managed
  preferences under the lock-down (`app/packages/cli/src/config-options.ts`),
  because they load above SpecTuner's own config, and on macOS the managed
  preferences are an organisation's OpenCode profile.
- **One folder per system** (`SpecTunerEnv.folder`), and the server always
  tells the sidecar where it is (`SPECTUNER_HOME`). **Why:** on Linux .NET
  derives the sidecar's default folder from `XDG_DATA_HOME`, which SpecTuner
  points into the server's home.
- **The desktop keeps its own `XDG_*` off Windows** and gives SpecTuner's only
  to the server process. **Why:** Linux desktops read them in the app itself
  (the browser opener, file dialogs).
- **Not brought back** from the starting code base: rpm, AppStream metadata,
  Intel Macs, Linux arm64, the Icon Composer icon, the command-line installer
  and the self-updater.

## Upgrading from builds made before the rename

The rename changed the names of SpecTuner's data folders and files, and an
earlier build's background service keeps the service port. On first start the
app stops that old service, only after confirming it is SpecTuner's own and
never an installed OpenCode's, then copies the database, saved credentials,
the local model settings, the desktop settings and saved window state to the
new names. It copies, never moves and never overwrites, and leaves the old
folder `%LOCALAPPDATA%\SpecTuner\opencode` in place. The code is
`app/packages/desktop/src/main/spectuner/upgrade.ts`.

**Not carried over.** Theme and notification choices kept by the window, and
sign-ins of two built-in providers whose storage keys changed.

## Language

### English only

With another language selected, the app showed a mix: the starting code base's
screens translated and SpecTuner's own text in English. The other locale files
were removed and the language is pinned to English. The translation mechanism
(keys and the English dictionaries) stays, so a language can be added back
once SpecTuner's own text is translated too.

## Testing

### MSTest for the sidecar, fakes instead of games

The sidecar's tests use MSTest, Microsoft's own framework, only in the test
project; nothing of it ships. Tests never launch a real game: they build fake
game folders (minimal PE headers, a stub `UnityEngine.dll` with the API the
probe uses) and a stand-in game exe that writes a result, crashes or hangs on
request. The TypeScript side uses a fake sidecar and a scripted model.

**Why.** Tests must run on any developer PC and in CI without a game or a model
key. What needs a real game is checked by hand on a copy.

### Platform differences live in one place, with a test switch

Everything the sidecar does differently on Windows, Linux and macOS sits
behind one `PlatformProfile` in `sidecar/src/SpecTuner.Sidecar/Platform/`:
the game layout, the loader package and how the game starts, the user
folders, and process handling. Code outside that folder never checks the
operating system. `SPECTUNER_PLATFORM` (`windows`, `linux`, `macos`) selects
a profile on any host; processes stay the host's. The fake game folders can be
shaped like any of the three systems' games (minimal PE, ELF or Mach-O
headers).

**Why.** The development machine runs only Windows. With the switch, the
Linux and macOS layout, scope gate, install, restore and launch environment
are tested there on every run, and the Windows behaviour stays exactly as it
was because its code path is separate. Tests that need a real process, signal
or file mode run only when the suite is run on Linux or macOS. **Limit.** None of this
proves that BepInEx loads into a real game on those systems; testers check
that. The app never sets the switch, and the server environment drops it.

### Remote testers send a diagnose report

The sidecar's `diagnose` command writes a read-only report on the system,
the game folder, the loader files, the logs and SpecTuner's record, with
paths, user name and computer name replaced by placeholders.

**Why.** Testers on Linux and macOS are not necessarily developers, and
nobody else can see their screens. One command that gathers the facts a
developer would ask for, and is safe to paste into a public report, replaces
a long exchange of questions.

## Icon

A performance gauge with a game-pad d-pad at its hub, on a graphite tile: the
gauge for measuring and tuning, the d-pad for games. Accent colour `#FF7A2F`.
It is made in Blender from `branding/make_icon.py` with no external assets,
with a simplified version for 32 pixels and below so it stays legible.
