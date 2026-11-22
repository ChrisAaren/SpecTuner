#!/usr/bin/env bun
import { cp, mkdir, rm, stat } from "node:fs/promises"
import path from "node:path"
import { SpecTunerConfig } from "@spectuner/modder/config"

// Fills resources/spectuner (git-ignored), which electron-builder ships outside app.asar and an unpackaged run reads:
//   spectuner-sidecar.exe  from SPECTUNER_SIDECAR, else <harness>/artifacts/sidecar/spectuner-sidecar.exe
//   plugin/                the tool plugin, copied as source from packages/modder/plugin
//   modder.txt             the modder agent's system prompt
//   licenses/              THIRD_PARTY_NOTICES.md and the licence texts from the harness repo
// Run from anywhere: bun packages/desktop/script/stage-spectuner.ts
const dir = path.resolve(import.meta.dir, "..")
const dest = path.join(dir, "resources", "spectuner")
const modder = path.resolve(dir, "../modder")
const sidecar = path.resolve(
  process.env.SPECTUNER_SIDECAR ?? path.resolve(dir, "../../../artifacts/sidecar/spectuner-sidecar.exe"),
)

await requireFile(sidecar)
await requireFile(path.join(modder, "plugin", "index.ts"))
await requireFile(SpecTunerConfig.PROMPT_FILE)

await rm(dest, { recursive: true, force: true })
await mkdir(dest, { recursive: true })
await cp(sidecar, path.join(dest, "spectuner-sidecar.exe"))
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
