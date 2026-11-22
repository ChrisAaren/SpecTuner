export * as SpecTunerUpgrade from "./upgrade"

import { execFile } from "node:child_process"
import { copyFileSync, existsSync, linkSync, mkdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs"
import path from "node:path"
import { DatabaseSync } from "node:sqlite"
import type { Info } from "@spectuner/client/service"

// One-time step for players upgrading from a SpecTuner build that still used opencode's names for its data. That
// build kept the server's data under `<LOCALAPPDATA>\SpecTuner\opencode\{data,config,state}\opencode\` and left a
// background service running from it on the port the new server uses. This step stops that service (only after
// confirming it is the old SpecTuner server) and copies what is worth keeping to the new names. It copies, never
// moves, never overwrites anything at the new location, and never deletes the old folders.
//
// Everything here is best effort: a failure is recorded and the app starts anyway.

/** The record of a finished run, written under the new home. Its presence with `complete: true` ends the step. */
export const MARKER = "upgraded-from-opencode.json"

/** The old build's executable name, as staged under `<userData>\cli\<version>-<arch>\`. */
export const OLD_SERVER_EXE = "opencode-cli.exe"

export interface Input {
  /** The old server home: `<LOCALAPPDATA>\SpecTuner\opencode` (packaged) or `opencode-dev` (unpackaged). */
  readonly oldHome: string
  /** The new server home: `<LOCALAPPDATA>\SpecTuner\app` (packaged) or `app-dev` (unpackaged). */
  readonly newHome: string
  /** The desktop's userData folder; its name did not change. */
  readonly userData: string
  /**
   * Folders the old build ran its server executable from. A process is the old service only if its image is an
   * `opencode-cli.exe` inside one of these (packaged: `<userData>\cli`).
   */
  readonly serverDirs: readonly string[]
}

export type Identity = "old" | "other" | "gone" | "unknown"

export interface Deps {
  /** The full path of a running process's executable, or undefined when it cannot be read. */
  readonly imagePath: (pid: number) => Promise<string | undefined>
  /** Whether a process with this id exists. */
  readonly alive: (pid: number) => boolean
  /** Stops the service registered in `file` and removes the registration (`@spectuner/client/service` `stop`). */
  readonly stop: (file: string) => Promise<void>
  /** Asks a registered service for its process id, as the old server answers: Basic auth with username `opencode`. */
  readonly infoPid: (info: Info) => Promise<number | undefined>
  readonly log: (message: string, data?: Record<string, unknown>) => void
}

export interface Step {
  readonly name: string
  readonly result: "copied" | "kept" | "absent" | "skipped" | "stopped" | "failed"
  readonly detail?: string
}

export interface Report {
  readonly status: "nothing-to-migrate" | "already-done" | "ran"
  readonly complete: boolean
  readonly steps: readonly Step[]
}

/** Old and new paths. The server's dirs changed twice: the home folder and the app subfolder inside each XDG dir. */
export function layout(input: Pick<Input, "oldHome" | "newHome" | "userData">) {
  const old = {
    data: path.join(input.oldHome, "data", "opencode"),
    config: path.join(input.oldHome, "config", "opencode"),
    state: path.join(input.oldHome, "state", "opencode"),
  }
  const next = {
    data: path.join(input.newHome, "data", "spectuner"),
    config: path.join(input.newHome, "config", "spectuner"),
  }

  return {
    registration: path.join(old.state, "service.json"),
    database: { from: path.join(old.data, "opencode.db"), to: path.join(next.data, "spectuner.db") },
    // Credentials from before the database held them; a copied database has already imported them.
    files: [
      { name: "legacy credentials", from: path.join(old.data, "auth.json"), to: path.join(next.data, "auth.json") },
      // The config the "Use a local model" form writes; it uses the last existing of the two names.
      { name: "config", from: path.join(old.config, "opencode.json"), to: path.join(next.config, "spectuner.json") },
      { name: "config", from: path.join(old.config, "opencode.jsonc"), to: path.join(next.config, "spectuner.jsonc") },
      {
        name: "desktop settings",
        from: path.join(input.userData, "opencode.settings"),
        to: path.join(input.userData, "spectuner.settings"),
      },
    ],
    drafts: path.join(input.userData, "drafts.sqlite"),
    marker: path.join(input.newHome, MARKER),
  }
}

export async function run(input: Input, deps: Deps): Promise<Report> {
  if (!existsSync(input.oldHome)) return { status: "nothing-to-migrate", complete: true, steps: [] }
  const paths = layout(input)

  if (readMarker(paths.marker)?.complete === true) return { status: "already-done", complete: true, steps: [] }

  const steps: Step[] = []
  const record = (step: Step) => {
    steps.push(step)
    deps.log(`upgrade: ${step.name} ${step.result}`, step.detail ? { detail: step.detail } : undefined)
  }
  const attempt = (name: string, work: () => Step["result"] | Step) => {
    try {
      const result = work()
      record(typeof result === "string" ? { name, result } : result)
    } catch (error) {
      record({ name, result: "failed", detail: message(error) })
    }
  }

  // The old service holds the port and writes the old database; the database is copied only once it is not running.
  const service = await stopOldService(paths.registration, input, deps).catch(
    (error): Step => ({ name: "old service", result: "failed", detail: message(error) }),
  )
  record(service)
  const serviceSettled = service.result !== "failed"

  if (!serviceSettled)
    record({ name: "database", result: "skipped", detail: "the old service may still be writing it" })
  else attempt("database", () => copyDatabase(paths.database.from, paths.database.to))

  for (const file of paths.files) attempt(file.name, () => copyIfMissing(file.from, file.to))

  attempt("renderer state", () => copyRendererState(paths.drafts))

  const complete = steps.every((step) => step.result !== "failed")

  try {
    writeMarker(paths.marker, { complete, at: new Date().toISOString(), from: input.oldHome, steps })
  } catch (error) {
    deps.log("upgrade: could not write the record", { error: message(error) })
  }

  return { status: "ran", complete, steps }
}

/**
 * Decides whether `pid` is the old SpecTuner server. Its executable path settles it: `opencode-cli.exe` inside one of
 * SpecTuner's own folders. An installed OpenCode runs from its own folders, so it is never taken for SpecTuner's.
 * Only if the path cannot be read does the server's own answer count: it must answer with the registration's password
 * for username `opencode` and report the same process id.
 */
export async function identify(info: Info, input: Pick<Input, "serverDirs">, deps: Deps): Promise<Identity> {
  if (!Number.isInteger(info.pid) || info.pid <= 0 || info.pid === process.pid) return "other"

  if (!deps.alive(info.pid)) return "gone"
  const image = await deps.imagePath(info.pid).catch(() => undefined)

  if (image) return isOldServer(image, input.serverDirs) ? "old" : "other"

  return (await deps.infoPid(info).catch(() => undefined)) === info.pid ? "old" : "unknown"
}

export function isOldServer(image: string, dirs: readonly string[]) {
  const file = path.win32.normalize(image).toLowerCase()

  if (path.win32.basename(file) !== OLD_SERVER_EXE) return false

  return dirs.some((dir) => file.startsWith(path.win32.normalize(dir).toLowerCase().replace(/\\+$/, "") + "\\"))
}

async function stopOldService(file: string, input: Input, deps: Deps): Promise<Step> {
  const name = "old service"

  if (!existsSync(file)) return { name, result: "absent" }
  const info = readRegistration(file)

  if (!info) return { name, result: "failed", detail: "the registration file is not readable" }
  const identity = await identify(info, input, deps)

  if (identity === "gone") return { name, result: "skipped", detail: `process ${info.pid} is not running` }

  if (identity === "other")
    return { name, result: "skipped", detail: `process ${info.pid} is not the old SpecTuner server; left alone` }

  if (identity === "unknown")
    return { name, result: "failed", detail: `could not confirm that process ${info.pid} is the old SpecTuner server` }
  await deps.stop(file)

  if (deps.alive(info.pid)) return { name, result: "failed", detail: `process ${info.pid} is still running` }

  return { name, result: "stopped", detail: `process ${info.pid}` }
}

function readRegistration(file: string): Info | undefined {
  try {
    const value: unknown = JSON.parse(readFileSync(file, "utf8"))

    if (typeof value !== "object" || value === null) return undefined
    const record = value as Record<string, unknown>

    if (typeof record.url !== "string" || typeof record.pid !== "number") return undefined

    return {
      url: record.url,
      pid: record.pid,
      id: typeof record.id === "string" ? record.id : undefined,
      version: typeof record.version === "string" ? record.version : undefined,
      password: typeof record.password === "string" ? record.password : undefined,
    }
  } catch {
    return undefined
  }
}

/**
 * Copies the database with its write-ahead log and shared-memory file, the database last. After the old service was
 * stopped hard, committed data can still be in the log, so the log must come along. The new database's existence is
 * what marks this copy as done: it is created last, atomically, and only if nothing is there yet.
 */
export function copyDatabase(from: string, to: string): Step["result"] | Step {
  if (existsSync(to)) return "kept"

  if (!existsSync(from)) return "absent"

  for (const suffix of ["-wal", "-shm"]) {
    const source = from + suffix
    const target = to + suffix

    if (!existsSync(source)) {
      if (existsSync(target))
        return { name: "database", result: "failed", detail: `${target} exists without a database; left alone` }
      continue
    }

    // Left by an earlier run that stopped before the database itself was copied: the same file again is fine.
    if (existsSync(target)) {
      if (!sameContent(source, target))
        return { name: "database", result: "failed", detail: `${target} exists and differs; left alone` }
      continue
    }

    copyIfMissing(source, target)
  }

  return copyIfMissing(from, to)
}

/** Copies `from` to `to` unless `to` exists. The target appears whole or not at all. */
export function copyIfMissing(from: string, to: string): "copied" | "kept" | "absent" {
  if (!existsSync(from)) return "absent"

  if (existsSync(to)) return "kept"
  mkdirSync(path.dirname(to), { recursive: true })
  const temp = `${to}.upgrade-${process.pid}.tmp`

  try {
    copyFileSync(from, temp)

    try {
      // A hard link fails if the target exists, so a file created meanwhile is never replaced.
      linkSync(temp, to)
    } catch (error) {
      if (code(error) === "EEXIST") return "kept"

      // File systems without hard links: rename, after one more check.
      if (existsSync(to)) return "kept"
      renameSync(temp, to)
    }

    return "copied"
  } finally {
    rmSync(temp, { force: true })
  }
}

/**
 * The renderer's saved state (layout, chosen model, tabs, drafts) lives in `drafts.sqlite` under names that started
 * with `opencode.` and now start with `spectuner.`; the rest of each name is unchanged. Copies each row to its new
 * name unless a row with that name exists. The old rows stay.
 */
export function copyRendererState(file: string): Step["result"] | Step {
  if (!existsSync(file)) return "absent"
  const db = new DatabaseSync(file)

  try {
    db.exec("PRAGMA busy_timeout = 5000")
    const tables = new Set(
      (db.prepare("SELECT name FROM sqlite_master WHERE type = 'table'").all() as Array<{ name: string }>).map(
        (row) => row.name,
      ),
    )
    let copied = 0
    db.exec("BEGIN IMMEDIATE")

    try {
      if (tables.has("state"))
        copied += Number(
          db
            .prepare(
              "INSERT OR IGNORE INTO state (name, key, value, updated_at) SELECT 'spectuner' || substr(name, 9), key, value, updated_at FROM state WHERE substr(name, 1, 9) = 'opencode.'",
            )
            .run().changes,
        )

      // Prompt drafts, keyed `<storage name>:<key>`.
      if (tables.has("document"))
        copied += Number(
          db
            .prepare(
              "INSERT OR IGNORE INTO document (key, value) SELECT 'spectuner' || substr(key, 9), value FROM document WHERE substr(key, 1, 9) = 'opencode.'",
            )
            .run().changes,
        )
      db.exec("COMMIT")
    } catch (error) {
      db.exec("ROLLBACK")
      throw error
    }

    return copied > 0 ? { name: "renderer state", result: "copied", detail: `${copied} rows` } : "kept"
  } finally {
    db.close()
  }
}

function sameContent(left: string, right: string) {
  if (statSync(left).size !== statSync(right).size) return false

  return readFileSync(left).equals(readFileSync(right))
}

function readMarker(file: string): { complete?: unknown } | undefined {
  try {
    const value: unknown = JSON.parse(readFileSync(file, "utf8"))

    return typeof value === "object" && value !== null ? (value as { complete?: unknown }) : undefined
  } catch {
    return undefined
  }
}

function writeMarker(file: string, value: unknown) {
  mkdirSync(path.dirname(file), { recursive: true })
  const temp = `${file}.${process.pid}.tmp`
  writeFileSync(temp, JSON.stringify(value, null, 2) + "\n")
  renameSync(temp, file)
}

function code(error: unknown) {
  return typeof error === "object" && error !== null && "code" in error ? String(error.code) : undefined
}

function message(error: unknown) {
  return error instanceof Error ? error.message : String(error)
}

// Production dependencies.

const LOOPBACK = new Set(["127.0.0.1", "localhost", "[::1]"])

export const defaultDeps = (log: Deps["log"]): Deps => ({
  imagePath: windowsImagePath,
  alive: (pid) => {
    try {
      process.kill(pid, 0)

      return true
    } catch (error) {
      return code(error) === "EPERM"
    }
  },
  stop: async (file) => {
    const { stop } = await import("@spectuner/client/service")
    await stop({ file })
  },
  infoPid: async (info) => {
    const url = new URL("/api/info", info.url)

    // The password goes to this machine only.
    if (url.protocol !== "http:" || !LOOPBACK.has(url.hostname)) return undefined
    const headers: Record<string, string> =
      info.password === undefined
        ? {}
        : { authorization: "Basic " + Buffer.from(`opencode:${info.password}`).toString("base64") }
    const response = await fetch(url, { headers, signal: AbortSignal.timeout(2_000) })

    if (response.status === 401 || response.status === 403 || response.status === 404) return undefined
    const body: unknown = await response.json()

    return typeof body === "object" && body !== null && "pid" in body && typeof body.pid === "number"
      ? body.pid
      : undefined
  },
  log,
})

/** The executable path of a process, read through Windows PowerShell. Undefined when it cannot be read. */
export function windowsImagePath(pid: number): Promise<string | undefined> {
  if (process.platform !== "win32" || !Number.isInteger(pid) || pid <= 0) return Promise.resolve(undefined)
  const shell = path.join(process.env.SystemRoot ?? "C:\\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe")

  return new Promise((resolve) => {
    execFile(
      shell,
      ["-NoProfile", "-NonInteractive", "-Command", `(Get-Process -Id ${pid} -ErrorAction Stop).Path`],
      { windowsHide: true, timeout: 10_000 },
      (error, stdout) => {
        const value = stdout?.toString().trim()
        resolve(!error && value ? value : undefined)
      },
    )
  })
}
