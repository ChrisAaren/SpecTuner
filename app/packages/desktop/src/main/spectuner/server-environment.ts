export * as SpecTunerServerEnvironment from "./server-environment"

import { mkdirSync, readFileSync, writeFileSync } from "node:fs"
import path from "node:path"
import { SpecTunerEnv } from "@spectuner/modder/env"

// SpecTuner's server must never meet an installed OpenCode: not its background service, registration file, database,
// config or port. The desktop spawns the service with its own `process.env`, so the SpecTuner environment is applied
// to that before anything can discover or start a service, and again wherever upstream code changes it.

/**
 * The service's port. An installed OpenCode listens on 28894 (0x70DE) on Windows. These stay in the 0x7000 block the
 * CLI picks on Windows (below the ranges Windows reserves for outgoing connections, Hyper-V and WSL). Unpackaged runs
 * get their own port and home, so a development build and the installed app can run side by side.
 */
export const PORT = { packaged: 0x7354, unpackaged: 0x7355 } as const

/** Files in the app's `resources/spectuner` folder. Packaging fails without them (`electron-builder.config.ts`). */
export const RESOURCES = {
  sidecar: "spectuner-sidecar.exe",
  plugin: "plugin",
  prompt: "modder.txt",
} as const

/**
 * Desktop switches that stay in the environment although they start with `SPECTUNER_`: the desktop sets them itself, or
 * reads them before the server starts, and the CLI ignores them. The desktop's server-selection switches
 * (`SPECTUNER_DESKTOP_ISOLATED_SERVER`, `_REUSE_SERVER`, `_REUSE_SERVICE_FILE`, `_SERVER_CHANNEL`, `_CLI_DEV`) are
 * dropped: they would point the app at another registration file or server.
 */
const DESKTOP_OWNED =
  /^SPECTUNER_(CLIENT|EXPERIMENTAL_ICON_DISCOVERY|EXPERIMENTAL_FILEWATCHER|DISABLE_EMBEDDED_WEB_UI|DESKTOP_(REMOTE_DEBUGGING_PORT|MEMORY_LOG|DISABLE_PROTOCOL_REGISTRATION|USER_DATA|TEST_ROOT))$/

export interface Input {
  readonly packaged: boolean
  /** The app's `resources/spectuner` folder. */
  readonly resources: string
  /** Set when the desktop runs in a test root (`SPECTUNER_DESKTOP_TEST_ROOT`); its data, config, cache and state are used. */
  readonly testRoot?: string
}

export interface Server {
  readonly home: string
  readonly port: number
  readonly sidecar: string
  readonly pluginDir: string
  readonly system: string
}

/** Where the server lives and what it loads. Reads the modder prompt from the resources. */
export function resolve(input: Input, base: Readonly<Record<string, string | undefined>>): Server {
  const home = SpecTunerEnv.defaultHome(base)
  return {
    home: input.testRoot ?? (input.packaged ? home : path.join(path.dirname(home), "app-dev")),
    port: input.packaged ? PORT.packaged : PORT.unpackaged,
    sidecar: path.join(input.resources, RESOURCES.sidecar),
    pluginDir: path.join(input.resources, RESOURCES.plugin),
    system: readFileSync(path.join(input.resources, RESOURCES.prompt), "utf8"),
  }
}

/**
 * Makes `env` the environment of a SpecTuner server: sets everything `SpecTunerEnv.make` sets, and removes every other
 * `SPECTUNER_*` and `XDG_*` variable except SpecTuner's own variables and the desktop's own switches. Safe to call
 * repeatedly.
 */
export function apply(env: NodeJS.ProcessEnv, server: Server) {
  const next = SpecTunerEnv.make({
    home: server.home,
    sidecar: server.sidecar,
    pluginDir: server.pluginDir,
    system: server.system,
    base: env,
  })
  Object.keys(env)
    .filter((key) => !(key in next) && /^(SPECTUNER_|XDG_)/i.test(key) && !SpecTunerEnv.OWN.test(key) && !DESKTOP_OWNED.test(key))
    .forEach((key) => delete env[key])
  Object.assign(env, next)
}

/**
 * Pins the service's port in its own service config (`<config>/spectuner/service.json`), which `spectuner serve
 * --service` reads before it falls back to the channel's default port. Other keys there (the service password) stay.
 */
export function pinPort(server: Server) {
  const file = path.join(SpecTunerEnv.dirs(server.home).config, "spectuner", "service.json")
  const current = readConfig(file)
  if (current.port === server.port) return
  mkdirSync(path.dirname(file), { recursive: true })
  writeFileSync(file, JSON.stringify({ ...current, port: server.port }, null, 2) + "\n", { mode: 0o600 })
}

// The service owns this file. A missing or unreadable one is replaced by the port alone; the service then writes a
// new password, as it does on its first start.
function readConfig(file: string): Record<string, unknown> {
  try {
    const value: unknown = JSON.parse(readFileSync(file, "utf8"))
    return typeof value === "object" && value !== null && !Array.isArray(value) ? { ...value } : {}
  } catch {
    return {}
  }
}
