# SpecTuner desktop

The Electron shell of SpecTuner: the window, the background server it starts,
and the packaging. Builds for Windows x64 (NSIS installer), Linux x64 (AppImage
and deb) and macOS on Apple Silicon (dmg and zip). Each system's build runs on
that system. The smoke test (`.github/workflows/smoke.yml`) builds all three
on GitHub's runners to check them; it keeps nothing.

For the whole build, use `./build.sh` in the repository root. It publishes the
sidecar, stages SpecTuner's resources, builds the server program, and runs the
two steps below. See [../../../CLAUDE.md](../../../CLAUDE.md).

## Build and pack by hand

```sh
# in app/packages/desktop
bun script/stage-spectuner.ts          # sidecar, plugin and prompt into resources/spectuner
SPECTUNER_CHANNEL=prod bun run build   # icons and the server program into resources/, the app into out/
SPECTUNER_CHANNEL=prod bun run pack    # the unpacked app into dist/desktop/app at the app/ root, and on Linux and
                                       # macOS the packages beside it
```

| Variable | Meaning |
|---|---|
| `SPECTUNER_CHANNEL` | `dev` (default), `beta`, or `prod`. Picks the app identity and data folders. Releases use `prod` |
| `SPECTUNER_VERSION` | Version stamped into the app and the server program |
| `SPECTUNER_DESKTOP_APP` | Windows: a folder holding an unpacked app. With it, `pack` builds the installer from that folder |
| `SPECTUNER_TARGET` | `windows-x64`, `linux-x64` or `darwin-arm64`; default this system |
| `SPECTUNER_HOMEPAGE` | Linux: the deb's homepage field. On GitHub Actions it defaults to the repository's address |

The server program (`spectuner-server.exe`, `spectuner-server` on Linux and
macOS), the window icon (none on macOS) and the `spectuner/` folder sit next to
`app.asar` in the packaged app's resources, and packaging fails if one is
missing or, on Linux and macOS, not executable. Builds are not code-signed; the
macOS app is signed ad hoc so its seal is valid.

## Run unpackaged

```sh
# in app/packages/desktop, after one build
node_modules/electron/dist/electron.exe .
```

On Linux the Electron binary is `node_modules/electron/dist/electron`, on macOS
`node_modules/electron/dist/Electron.app/Contents/MacOS/Electron`.

An unpackaged run uses the `dev` identity: its own data folders
(`%LOCALAPPDATA%\SpecTuner\app-dev`, `%APPDATA%\com.spectuner.desktop.dev` on
Windows; see `docs/development.md` for Linux and macOS) and
its own service port, so it can run next to an installed SpecTuner. Developer
tools are available in an unpackaged run and switched off in a packaged build.

## Where SpecTuner's wiring is

| What | File |
|---|---|
| The server's environment (folders, lock-down) | `src/main/spectuner/server-environment.ts` |
| One-time upgrade from builds made before the rename | `src/main/spectuner/upgrade.ts` |
| Bundling the sidecar, plugin and prompt | `script/stage-spectuner.ts` |
| Icons | `icons/<channel>/` (see `icons/README.md`), copied by `script/copy-icons.ts` |
| The server's environment on Linux and macOS (passed at spawn, not set in the app's own) | `src/main/spectuner/server-environment.ts` (`spawnEnv`, `registrationFile`) |
| Packaging | `electron-builder.config.ts`, `script/pack.ts` |
