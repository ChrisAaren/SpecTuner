#!/usr/bin/env bun
import { $ } from "bun"
import { exists } from "node:fs/promises"
import path from "node:path"
import { copyBuiltCliToResources, getCurrentCli, resolveChannel } from "./utils"
import { verifyBundles } from "./verify-bundles"

// Prepares resources/ (icons, the bundled CLI) for SPECTUNER_CHANNEL and builds the app into out/.
// The CLI comes from ../cli/dist, which must hold the target; otherwise the build fails.
const dir = path.resolve(import.meta.dir, "..")

const cli = path.resolve(dir, "../cli/dist")

const channel = resolveChannel()

process.chdir(dir)

await $`bun ./script/copy-icons.ts ${channel}`

const built = await exists(path.join(cli, getCurrentCli().package.replace("@spectuner/", "")))

// SpecTuner bundles only the fork's own CLI, built from this checkout, on every channel; never upstream's from npm.
if (!built) throw new Error(`SpecTuner desktop builds bundle the fork's CLI from ${cli}; build it first (see build.sh)`)

await copyBuiltCliToResources(cli)

await $`bunx electron-vite build`

await verifyBundles(path.join(dir, "out"))
