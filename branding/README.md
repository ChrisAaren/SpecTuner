# SpecTuner icon and logo mark

**Concept.** A performance gauge whose arc is lit orange up to a needle that points into the upper right ("tuned up"), with the needle pivoting on a game-pad d-pad: your PC's performance and the game, tuned together. At 32 px and below the d-pad hub becomes a plain dot and the strokes get thicker, so the gauge alone carries the mark down to 16 px.

## Palette

| Role | Hex | Notes |
|---|---|---|
| Accent, "SpecTuner orange" | `#FF7A2F` | UI accent; the lit arc and hub dot. The 3D arc runs `#FF6A26` to `#FFA63D` |
| Accent, warm end | `#FFA63D` | Only for gradients next to the accent |
| Needle and d-pad | `#F2F3F6` | Off-white |
| Tile, graphite | `#232733` | 3D tile runs `#1C1F29` (bottom) to `#2C3140` (top) |
| Track (unlit arc) | `#3B4150` | `#4A5163` in the flat colour mark, so it shows on dark UI |

Two colours (orange, off-white) plus graphite neutrals. The tile keeps the mark legible on both light and dark taskbars.

## Files

| File | What it is for |
|---|---|
| `icon.ico` | Windows app, installer and window icon. 16, 24, 32, 48, 64 px as 32-bit DIB entries with AND mask; 128 and 256 px as PNG entries |
| `icon-1024.png` ... `icon-16.png` | Transparent PNGs at 1024, 512, 256, 128, 64, 48, 32, 24, 16 px. 32 px and smaller use the simplified variant |
| `logo-mark.svg` | Flat mark for the UI, all `currentColor` (track at 35% opacity), so CSS `color` tints it. 64 x 64 viewBox |
| `logo-mark-color.svg` | Same mark in the palette colours, for dark backgrounds (its needle is off-white) |
| `contact-sheet.png` | Review sheet: icon at 256, 64, 32, 16 (32 and 16 also enlarged 4x) on `#16161a` and `#f2f2f4`, then the flat colour mark and the tinted mono mark at 128, 48 and 16 |
| `make_icon.py` | Blender script that builds both scenes (full for 48 px and up, simplified for 32 px and below) from nothing and writes every file above |
| `verify_ico.ts` | Parses `icon.ico` back, lists its entries and checks each one |

## Regenerate

From Git Bash in this folder (about 40 seconds on CPU):

```sh
blender -b --factory-startup -P make_icon.py -- --all
bun verify_ico.ts
```

`blender` is Blender 5.2 or later (use the full path to `blender.exe` if it is not on `PATH`). Intermediate renders go to the system temp folder and are deleted afterwards.

No `.blend` files are kept in the repository: Blender records details of the machine that saved a file in it. To open the scenes in Blender, add `--blend-dir <folder>` with a scratch folder outside the repository; `--all` then also saves `icon.blend` and `icon-small.blend` there.

One concept or size on its own:

```sh
blender -b --factory-startup -P make_icon.py -- \
  --concept gauge --variant full --size 256 --out /tmp/test-256.png
```

`--concept` takes `gauge` (chosen), `dpad`, `chip` or `pad` (rejected explorations). `--variant small` is the simplified version for 32 px and below.

## Where the app uses these files

- `icon.ico` is copied to `app/packages/desktop/icons/<channel>/icon.ico` (`dev`, `beta`, `prod`, the same file in each). The desktop build uses it for the exe, the installer, the window and the taskbar.
- `icon-128.png` is the notification icon (`app/packages/desktop/src/renderer/platform/notification-icon.png`).
- The favicons and app icons in `app/packages/app/public` and `app/packages/ui/src/assets/favicon` were resized from `icon-1024.png`.
- `logo-mark.svg` and `logo-mark-color.svg` are copied to `app/packages/ui/src/assets/logo/`; the UI's logo components draw the same paths.

After regenerating, copy the files to those places again.

Everything here is made by `make_icon.py` with no external assets, fonts, textures or HDRIs.
