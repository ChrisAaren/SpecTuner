# Built-in GUI plugins

SpecTuner's built-in window features that use the GUI plugin SDK in [`packages/plugin/src/gui`](../plugin/src/gui)
(`@spectuner/plugin/gui`). Today there is one: the Changes panel, [`src/spectuner-changes`](src/spectuner-changes),
which lists the changes made to the open game, each with its own undo.

- [`src/renderer.ts`](src/renderer.ts) and [`src/main.ts`](src/main.ts) are the only files that list the built-ins,
  each through `compose` from `@spectuner/plugin/gui/host`. The window host is in `packages/app/src/runtime/plugin/`,
  the main-process host in `packages/desktop/src/main/plugin/`.
- [`src/schemes.ts`](src/schemes.ts) lists URL schemes a built-in registers before Electron is ready. None does today.
- One folder per built-in: `index.ts` (`Plugin.define({ os?, stores?, icons?, i18n })`), `renderer.tsx`, an optional
  `main.ts`, and `i18n/en.ts`.
- Rules for agents: [`AGENTS.md`](AGENTS.md).

The upstream editor shipped about fifteen more built-ins here (terminal, files, review, browser, SSH, WSL, updater and
others). SpecTuner never showed them, so they were removed rather than switched off.
