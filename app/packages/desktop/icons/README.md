# Desktop icons

Every channel (`dev`, `beta`, `prod`) uses the same SpecTuner icon. The source files and the script that makes them
are in `branding/` at the top of the SpecTuner repository (`branding/README.md`).

`script/copy-icons.ts` copies `icons/<channel>/` to `resources/icons/` before a build or a pack.

| File | Source | Used by |
|---|---|---|
| `icon.ico` | `branding/icon.ico` | The exe, the installer and uninstaller, the window and the taskbar (`electron-builder.config.ts`, `src/main/windows/early.ts`, `src/main/windows/appearance.ts`) |

The web UI build (`packages/app/vite.icons.ts`) takes its icons from `packages/app/public`, not from here.
