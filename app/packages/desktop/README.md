# SpecTuner desktop

The Electron shell of SpecTuner: the window, the background server it starts,
and the packaging. Windows x64 only.

For the whole build, use `./build.sh` in the repository root. It publishes the
sidecar, stages SpecTuner's resources, builds the server program, and runs the
two steps below. See [../../../CLAUDE.md](../../../CLAUDE.md).

## Build and pack by hand

```sh
# in app/packages/desktop
bun script/stage-spectuner.ts          # sidecar, plugin and prompt into resources/spectuner
SPECTUNER_CHANNEL=prod bun run build   # icons and the server program into resources/, the app into out/
SPECTUNER_CHANNEL=prod bun run pack    # the unpacked app into dist/desktop/app at the app/ root
```

| Variable | Meaning |
|---|---|
| `SPECTUNER_CHANNEL` | `dev` (default), `beta`, or `prod`. Picks the app identity and data folders. Releases use `prod` |
| `SPECTUNER_VERSION` | Version stamped into the app and the server program |
| `SPECTUNER_DESKTOP_APP` | A folder holding an unpacked app. With it, `pack` builds the installer from that folder |

The server program (`spectuner-server.exe`), the window icon and the
`spectuner/` folder sit next to `app.asar` in the packaged app's resources, and
packaging fails if one is missing. Builds are not code-signed.

## Run unpackaged

```sh
# in app/packages/desktop, after one build
node_modules/electron/dist/electron.exe .
```

An unpackaged run uses the `dev` identity: its own data folders
(`%LOCALAPPDATA%\SpecTuner\app-dev`, `%APPDATA%\com.spectuner.desktop.dev`) and
its own service port, so it can run next to an installed SpecTuner. Developer
tools are available in an unpackaged run and switched off in a packaged build.

## Where SpecTuner's wiring is

| What | File |
|---|---|
| The server's environment (folders, lock-down) | `src/main/spectuner/server-environment.ts` |
| One-time upgrade from builds made before the rename | `src/main/spectuner/upgrade.ts` |
| Bundling the sidecar, plugin and prompt | `script/stage-spectuner.ts` |
| Icons | `icons/<channel>/icon.ico`, copied by `script/copy-icons.ts` |
| Packaging | `electron-builder.config.ts`, `script/pack.ts` |
