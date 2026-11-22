export * as SpecTunerServer from "./server.ts"

import { spawn } from "node:child_process"
import { randomBytes } from "node:crypto"
import { mkdirSync } from "node:fs"
import path from "node:path"
import { SpecTunerEnv } from "../src/env.ts"

export const PACKAGE_DIR = path.resolve(import.meta.dir, "..")
export const PLUGIN_DIR = path.join(PACKAGE_DIR, "plugin")
export const CLI_DIR = path.resolve(PACKAGE_DIR, "..", "cli")
/** Where the other engineer publishes the sidecar, relative to this checkout (`<harness>/<app folder>/packages/modder`). */
export const DEFAULT_SIDECAR = path.resolve(
  PACKAGE_DIR,
  "..",
  "..",
  "..",
  "artifacts",
  "sidecar",
  "spectuner-sidecar.exe",
)

export interface Options {
  /** Server home (XDG dirs live under it). */
  readonly home: string
  readonly sidecar: string
  readonly pluginDir?: string
  /** A compiled `spectuner.exe`. Without it the CLI runs from source with this Bun. */
  readonly exe?: string
  readonly port?: number
  /** Extra variables on top of the SpecTuner environment (for example `SPECTUNER_HOME`). */
  readonly env?: Readonly<Record<string, string>>
  /** Echo the server's output to this process. */
  readonly echo?: boolean
}

export interface Server {
  readonly url: string
  readonly password: string
  readonly pid: number
  /** Everything the server printed so far. */
  readonly output: () => string
  readonly fetch: (route: string, init?: { method?: string; body?: unknown }) => Promise<Response>
  readonly stop: () => Promise<void>
}

/** Starts `spectuner serve` in the default foreground mode on an explicit port, with the SpecTuner environment. */
export async function start(options: Options): Promise<Server> {
  const port = options.port ?? freePort()
  const password = randomBytes(18).toString("base64url")
  const dirs = SpecTunerEnv.dirs(options.home)
  Object.values(dirs).forEach((dir) => mkdirSync(dir, { recursive: true }))
  const env = {
    ...SpecTunerEnv.make({ home: options.home, sidecar: options.sidecar, pluginDir: options.pluginDir ?? PLUGIN_DIR }),
    ...options.env,
    SPECTUNER_PASSWORD: password,
  }
  const command = options.exe
    ? { file: options.exe, args: ["serve", "--port", String(port)], cwd: options.home }
    : { file: process.execPath, args: ["run", "src/index.ts", "serve", "--port", String(port)], cwd: CLI_DIR }
  const child = spawn(command.file, command.args, { cwd: command.cwd, env, windowsHide: true })
  const output = { text: "" }
  const append = (chunk: Buffer) => {
    output.text += chunk.toString("utf8")
    if (options.echo) process.stdout.write(chunk)
  }
  child.stdout.on("data", append)
  child.stderr.on("data", append)
  const exited = new Promise<number | null>((resolve) => child.on("exit", (code) => resolve(code)))
  const url = `http://127.0.0.1:${port}`
  const authorization = `Basic ${Buffer.from(`spectuner:${password}`).toString("base64")}`
  const server: Server = {
    url,
    password,
    pid: child.pid ?? -1,
    output: () => output.text,
    fetch: (route, init) =>
      fetch(`${url}${route}`, {
        method: init?.method ?? (init?.body === undefined ? "GET" : "POST"),
        headers: { authorization, ...(init?.body === undefined ? {} : { "content-type": "application/json" }) },
        body: init?.body === undefined ? undefined : JSON.stringify(init.body),
      }),
    stop: async () => {
      if (child.exitCode === null && child.signalCode === null) child.kill()
      await exited
    },
  }
  const ready = await Promise.race([
    until(() => output.text.includes("server listening on"), 90_000).then(() => true),
    exited.then(() => false),
  ])
  if (!ready) throw new Error(`SpecTuner server exited during startup:\n${output.text}`)
  return server
}

/** `?location[directory]=...` for Location-scoped routes. */
export function location(directory: string) {
  return `location%5Bdirectory%5D=${encodeURIComponent(directory)}`
}

/** Polls `check` until it returns a value, or fails after `timeoutMs`. */
export async function until<T>(check: () => T | undefined | false | Promise<T | undefined | false>, timeoutMs: number) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const value = await check()
    if (value) return value
    await Bun.sleep(50)
  }
  throw new Error(`Timed out after ${timeoutMs}ms`)
}

function freePort() {
  const listener = Bun.listen({ hostname: "127.0.0.1", port: 0, socket: { data() {} } })
  const port = listener.port
  listener.stop(true)
  return port
}
