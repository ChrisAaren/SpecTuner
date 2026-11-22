#!/usr/bin/env bun
import { $ } from "bun"
import { cp, mkdir, rm } from "node:fs/promises"
import path from "node:path"
import { getCurrentCli, resolveChannel } from "./utils"

// Packages the built app (out/) for Windows into dist/desktop at the repository root, unsigned: the unpacked app in
// app/, or with SPECTUNER_DESKTOP_APP, the NSIS installer around that (signed) app.
const dir = path.resolve(import.meta.dir, "..")

const out = path.resolve(dir, "../../dist/desktop")

const cli = getCurrentCli()

const app = process.env.SPECTUNER_DESKTOP_APP

const flags = ["--win", `--${cli.cpu}`, "--publish", "never", "--config", "electron-builder.config.ts"]

process.chdir(dir)

await rm("dist", { recursive: true, force: true })

await rm(out, { recursive: true, force: true })

await mkdir(out, { recursive: true })

if (app) {
  // The installer uses the channel's icons; the rest of the app is already built and signed.
  await $`bun ./script/copy-icons.ts ${resolveChannel()}`
  await $`bunx electron-builder ${flags} --prepackaged ${app}`
  await cp(`dist/spectuner-desktop-win-${cli.cpu}.exe`, path.join(out, `spectuner-desktop-win-${cli.cpu}.exe`))
}

if (!app) {
  await $`bunx electron-builder ${flags} --dir`
  await cp(path.join("dist", "win-unpacked"), path.join(out, "app"), { recursive: true })
}
