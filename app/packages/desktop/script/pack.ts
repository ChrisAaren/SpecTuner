#!/usr/bin/env bun
import { $ } from "bun"
import { cp, mkdir, readdir, rm } from "node:fs/promises"
import path from "node:path"
import { getCurrentCli, resolveChannel } from "./utils"

// Packages the built app (out/) for SPECTUNER_TARGET (default: this system) into dist/desktop at the repository root,
// unsigned:
//   Windows: the unpacked app in app/, or with SPECTUNER_DESKTOP_APP, the NSIS installer around that (signed) app.
//   Linux:   the AppImage and the deb, and the unpacked app in app/ (for checks).
//   macOS:   the dmg and the zip (signed ad hoc, see electron-builder.config.ts), and the .app in app/ (for checks).
const dir = path.resolve(import.meta.dir, "..")

const out = path.resolve(dir, "../../dist/desktop")

const cli = getCurrentCli()

const os = new Map([
  ["darwin", "mac"],
  ["win32", "win"],
  ["linux", "linux"],
]).get(cli.os)

if (!os) throw new Error(`No packaging for ${cli.os}`)

const app = process.env.SPECTUNER_DESKTOP_APP

if (app && os !== "win") throw new Error("SPECTUNER_DESKTOP_APP (an installer around a prepared app) is for Windows only")

const flags = [`--${os}`, `--${cli.cpu}`, "--publish", "never", "--config", "electron-builder.config.ts"]

process.chdir(dir)

await rm("dist", { recursive: true, force: true })

await rm(out, { recursive: true, force: true })

await mkdir(out, { recursive: true })

if (os === "win" && app) {
  // The installer uses the channel's icons; the rest of the app is already built and signed.
  await $`bun ./script/copy-icons.ts ${resolveChannel()}`
  await $`bunx electron-builder ${flags} --prepackaged ${app}`
  await cp(`dist/spectuner-desktop-win-${cli.cpu}.exe`, path.join(out, `spectuner-desktop-win-${cli.cpu}.exe`))
}

if (os === "win" && !app) {
  await $`bunx electron-builder ${flags} --dir`
  await cp(path.join("dist", "win-unpacked"), path.join(out, "app"), { recursive: true })
}

if (os === "linux") {
  await $`bunx electron-builder ${flags}`
  const files = (await readdir("dist")).filter((file) => /\.(AppImage|deb)$/.test(file))

  if (files.length !== 2) throw new Error(`Expected one AppImage and one deb in dist, found ${files.join(", ") || "none"}`)
  await Promise.all(files.map((file) => cp(path.join("dist", file), path.join(out, file))))
  await cp(path.join("dist", cli.cpu === "arm64" ? "linux-arm64-unpacked" : "linux-unpacked"), path.join(out, "app"), {
    recursive: true,
  })
}

if (os === "mac") {
  await $`bunx electron-builder ${flags}`
  const files = (await readdir("dist")).filter((file) => /\.(dmg|zip)$/.test(file))

  if (files.length !== 2) throw new Error(`Expected one dmg and one zip in dist, found ${files.join(", ") || "none"}`)
  await Promise.all(files.map((file) => cp(path.join("dist", file), path.join(out, file))))
  const parent = path.join("dist", cli.cpu === "arm64" ? "mac-arm64" : "mac")
  const bundles = (await readdir(parent)).filter((file) => file.endsWith(".app"))

  if (bundles.length !== 1) throw new Error(`Expected one .app bundle in ${parent}, found ${bundles.join(", ")}`)
  await mkdir(path.join(out, "app"), { recursive: true })
  // ditto keeps the bundle's symbolic links and extended attributes, which its signature covers.
  await $`ditto ${path.join(parent, bundles[0])} ${path.join(out, "app", bundles[0])}`
}
