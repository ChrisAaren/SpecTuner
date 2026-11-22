# Desktop icons

Every channel (`dev`, `beta`, `prod`) uses the same SpecTuner icon. The source files and the script that makes them
are in `branding/` at the top of the SpecTuner repository (`branding/README.md`).

`script/copy-icons.ts` copies `icons/<channel>/` to `resources/icons/` before a build or a pack.

| File | Source | Used by |
|---|---|---|
| `icon.ico` | `branding/icon.ico` | Windows: the exe, the installer and uninstaller, the window and the taskbar (`electron-builder.config.ts`, `src/main/windows/early.ts`, `src/main/windows/appearance.ts`) |
| `icon.icns` | `branding/icon.icns` (`make_icon.py --mac`) | macOS: the app bundle, the Dock and the dmg |
| `16x16.png` ... `512x512.png` | `branding/icon-16.png` ... `icon-512.png` | Linux: the icon theme the deb and AppImage install (`linux.icon`) |
| `icon.png` | `branding/icon-512.png` | Linux: the window icon (`src/main/windows/icon.ts`) |

`electron-builder.config.test.ts` checks that each file is a byte-for-byte copy of its source.

The web UI build (`packages/app/vite.icons.ts`) takes its icons from `packages/app/public`, not from here.
