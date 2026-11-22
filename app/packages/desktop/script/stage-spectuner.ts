#!/usr/bin/env bun
import { chmod, cp, mkdir, rm, stat } from "node:fs/promises"
import path from "node:path"
import { SpecTunerConfig } from "@spectuner/modder/config"
import { SpecTunerEnv } from "@spectuner/modder/env"

// Fills resources/spectuner (git-ignored), which electron-builder ships outside app.asar and an unpackaged run reads:
//   spectuner-sidecar[.exe] from SPECTUNER_SIDECAR, else where sidecar/publish.sh writes it for this system:
//                          <harness>/artifacts/sidecar/spectuner-sidecar.exe on Windows,
//                          <harness>/artifacts/sidecar/<rid>/spectuner-sidecar elsewhere (linux-x64, osx-arm64)
//   plugin/                the tool plugin, copied as source from packages/modder/plugin
//   modder.txt             the modder agent's system prompt
//   licenses/              THIRD_PARTY_NOTICES.md and the licence texts from the harness repo
// Run from anywhere: bun packages/desktop/script/stage-spectuner.ts
const dir = path.resolve(import.meta.dir, "..")
const dest = path.join(dir, "resources", "spectuner")
const modder = path.resolve(dir, "../modder")
const sidecar = path.resolve(
  process.env.SPECTUNER_SIDECAR ?? SpecTunerEnv.publishedSidecar(path.resolve(dir, "../../../artifacts")),
)
const sidecarName = SpecTunerEnv.sidecarFile()

await requireFile(sidecar)
await requireFile(path.join(modder, "plugin", "index.ts"))
await requireFile(SpecTunerConfig.PROMPT_FILE)

await rm(dest, { recursive: true, force: true })
await mkdir(dest, { recursive: true })
await cp(sidecar, path.join(dest, sidecarName))
// The server starts the sidecar as a program; a build artifact downloaded between jobs may have lost the bit.
if (process.platform !== "win32") await chmod(path.join(dest, sidecarName), 0o755)
await cp(path.join(modder, "plugin"), path.join(dest, "plugin"), { recursive: true })
await cp(SpecTunerConfig.PROMPT_FILE, path.join(dest, "modder.txt"))

// Licence notices for what the sidecar bundles and installs into games (BepInEx, UnityDoorstop, HarmonyX, ...).
// UnityDoorstop is LGPL-2.1, so its licence text ships with the app.
const harness = path.resolve(dir, "../../..")
await requireFile(path.join(harness, "THIRD_PARTY_NOTICES.md"))
await cp(path.join(harness, "THIRD_PARTY_NOTICES.md"), path.join(dest, "licenses", "THIRD_PARTY_NOTICES.md"))
await cp(path.join(harness, "licenses"), path.join(dest, "licenses"), { recursive: true })

console.log(`Staged SpecTuner resources in ${dest} (sidecar from ${sidecar})`)

async function requireFile(file: string) {
  const info = await stat(file).catch(() => undefined)
  if (!info?.isFile() || info.size === 0) throw new Error(`SpecTuner resource missing or empty: ${file}`)
}
