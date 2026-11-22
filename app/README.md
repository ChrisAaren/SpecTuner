# SpecTuner app code

This folder is the application code of SpecTuner: the Electron desktop app,
its UI, the local server it starts, and the modder agent with its `game_*`
tools. It is a Bun workspace; the packages are in `packages/`.

The code started from [opencode](https://github.com/anomalyco/opencode) 2.3.0
(commit `dcb24c74dc`) and is now an independent code base. The opencode code
is used under the MIT licence; its text is in [LICENSE](LICENSE).
`CHANGELOG.md` is opencode's changelog up to that point and is kept for
reference.

## Packages SpecTuner adds or relies on most

| Package | What it is |
|---|---|
| `packages/modder` | The modder agent: its tools (`plugin/`), prompt (`src/prompt/modder.txt`), lock-down config (`src/config.ts`), server environment (`src/env.ts`) and development scripts (`script/`) |
| `packages/desktop` | The Electron shell. SpecTuner's wiring is in `src/main/spectuner/`; `script/stage-spectuner.ts` bundles the sidecar, plugin and prompt |
| `packages/app` | The window's screens. SpecTuner's choices are in `src/spectuner/` |
| `packages/gui-plugins` | The Changes panel, in `src/spectuner-changes/` |
| `packages/server` | The HTTP API, including the lock-down refusals in `src/middleware/lockdown.ts` |
| `packages/core` | Agent loop, sessions, permissions, providers, tool registry |
| `packages/cli` | Builds the server program the desktop app starts |

## Quick start

```sh
# in app/
bun install
# in app/packages/modder
bun typecheck
bun run test
```

The C# helper program the tools call lives in `../sidecar`. Building the whole
app, running it unpackaged, every test suite with its known failures, and the
rules for changing the code are in [../CLAUDE.md](../CLAUDE.md). What
SpecTuner is, for players, is in [../README.md](../README.md).
