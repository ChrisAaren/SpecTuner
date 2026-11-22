import { afterEach, beforeEach, describe, expect, test } from "bun:test"
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs"
import { mkdtemp, rm } from "node:fs/promises"
import os from "node:os"
import path from "node:path"
import { DatabaseSync } from "node:sqlite"
import type { Subprocess } from "bun"
import { SpecTunerUpgrade } from "./upgrade"

// Every test builds the old and new layouts in a temporary folder; nothing here reads or writes a real profile.
const root = { dir: "" }
const children: Subprocess[] = []

beforeEach(async () => {
  root.dir = await mkdtemp(path.join(os.tmpdir(), "spectuner-upgrade-"))
})

afterEach(async () => {
  for (const child of children.splice(0)) child.kill()
  await rm(root.dir, { recursive: true, force: true })
})

const input = (): SpecTunerUpgrade.Input => {
  const local = path.join(root.dir, "Local", "SpecTuner")
  const userData = path.join(root.dir, "Roaming", "com.spectuner.desktop")

  return {
    oldHome: path.join(local, "opencode"),
    newHome: path.join(local, "app"),
    userData,
    serverDirs: [path.join(userData, "cli")],
  }
}

const paths = () => SpecTunerUpgrade.layout(input())

const write = (file: string, content: string | Buffer) => {
  mkdirSync(path.dirname(file), { recursive: true })
  writeFileSync(file, content)
}

const read = (file: string) => readFileSync(file, "utf8")

type Calls = { stop: string[]; imagePath: number[]; infoPid: number[]; logs: string[] }

const deps = (overrides: Partial<SpecTunerUpgrade.Deps> = {}) => {
  const calls: Calls = { stop: [], imagePath: [], infoPid: [], logs: [] }
  const real = SpecTunerUpgrade.defaultDeps((message) => calls.logs.push(message))
  const value: SpecTunerUpgrade.Deps = {
    ...real,
    imagePath: async (pid) => {
      calls.imagePath.push(pid)

      return undefined
    },
    stop: async (file) => {
      calls.stop.push(file)
      await real.stop(file)
    },
    infoPid: async (info) => {
      calls.infoPid.push(info.pid)

      return real.infoPid(info)
    },
    ...overrides,
  }

  return { deps: value, calls }
}

/** The old build's data: a database whose last write is still in its write-ahead log, config, settings, drafts. */
function oldInstall(options: { openWriter?: boolean } = {}) {
  const p = paths()
  mkdirSync(path.dirname(p.database.from), { recursive: true })
  const db = new DatabaseSync(p.database.from)
  db.exec("PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0")
  db.exec("CREATE TABLE credential (integration_id TEXT PRIMARY KEY, secret TEXT)")
  db.exec("INSERT INTO credential VALUES ('anthropic', 'sk-old')")

  write(p.files[2].from, '{ "provider": { "spectuner-local": {} } }\n')
  write(p.files[0].from, '{ "openai": { "type": "api", "key": "x" } }\n')
  write(p.files[3].from, '{ "zoomLevel": 1.2 }\n')

  const drafts = new DatabaseSync(p.drafts)
  drafts.exec("CREATE TABLE state (name TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY (name, key))")
  drafts.exec("CREATE TABLE document (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
  drafts.exec("INSERT INTO state VALUES ('opencode.global.dat', 'model', '\"anthropic/claude\"', 1)")
  drafts.exec("INSERT INTO state VALUES ('opencode.window.w1.dat', 'layout', '{}', 1)")
  drafts.exec("INSERT INTO state VALUES ('other.dat', 'x', '1', 1)")
  drafts.exec("INSERT INTO document VALUES ('opencode.draft.abc.123.dat:prompt', 'double the jump height')")
  drafts.close()

  // A hard-terminated service leaves its last commits in the log; keep the writer open while copying to model that.
  if (options.openWriter) return db
  db.close()

  return undefined
}

function credentials(file: string) {
  const db = new DatabaseSync(file, { readOnly: true })

  try {
    return db.prepare("SELECT integration_id, secret FROM credential").all()
  } finally {
    db.close()
  }
}

function stateRows(file: string) {
  const db = new DatabaseSync(file, { readOnly: true })

  try {
    return {
      state: db.prepare("SELECT name, key, value FROM state ORDER BY name, key").all(),
      document: db.prepare("SELECT key, value FROM document ORDER BY key").all(),
    }
  } finally {
    db.close()
  }
}

/** A stand-in for the old service: answers /api/info only for username `opencode` and its password. */
async function oldService(password: string) {
  const script = path.join(root.dir, "old-service.ts")
  write(
    script,
    `const password = process.argv[2]
const server = Bun.serve({
  hostname: "127.0.0.1",
  port: 0,
  fetch(request) {
    if (request.headers.get("authorization") !== "Basic " + btoa("opencode:" + password)) return new Response("", { status: 401 })
    return Response.json({ version: "2.3.0-spectuner.1", pid: process.pid })
  },
})
console.log(server.port)
`,
  )
  const child = Bun.spawn([process.execPath, script, password], { stdout: "pipe", stderr: "ignore" })
  children.push(child)
  const reader = child.stdout.getReader()
  let text = ""

  while (!text.includes("\n")) {
    const chunk = await reader.read()

    if (chunk.done) throw new Error("the stand-in service exited")
    text += new TextDecoder().decode(chunk.value)
  }
  reader.releaseLock()

  return { child, url: `http://127.0.0.1:${text.trim()}` }
}

function register(info: { url: string; pid: number; password?: string }) {
  write(paths().registration, JSON.stringify({ id: "svc", version: "2.3.0-spectuner.1", ...info }))
}

const alive = (pid: number) => {
  try {
    process.kill(pid, 0)

    return true
  } catch {
    return false
  }
}

const oldExe = () => path.join(input().userData, "cli", "2.3.0-spectuner.1-x64", "opencode-cli.exe")

describe("upgrade from the build with opencode's names", () => {
  test("nothing to migrate: no old home, nothing written", async () => {
    const { deps: d, calls } = deps()
    const report = await SpecTunerUpgrade.run(input(), d)
    expect(report).toEqual({ status: "nothing-to-migrate", complete: true, steps: [] })
    expect(existsSync(input().newHome)).toBe(false)
    expect(calls.stop).toEqual([])
  })

  test("full migration copies database with its log, config, credentials, settings and renderer state", async () => {
    const writer = oldInstall({ openWriter: true })
    const p = paths()
    expect(existsSync(p.database.from + "-wal")).toBe(true)
    const { deps: d, calls } = deps()

    try {
      const report = await SpecTunerUpgrade.run(input(), d)
      expect(report.status).toBe("ran")
      expect(report.complete).toBe(true)
      expect(Object.fromEntries(report.steps.map((step) => [step.name, step.result]))).toEqual({
        "old service": "absent",
        database: "copied",
        "legacy credentials": "copied",
        config: "copied",
        "desktop settings": "copied",
        "renderer state": "copied",
      })
    } finally {
      writer?.close()
    }

    expect(calls.stop).toEqual([])
    // The last commit was only in the write-ahead log; the copy has it.
    expect(credentials(p.database.to)).toEqual([{ integration_id: "anthropic", secret: "sk-old" }])
    expect(read(path.join(input().newHome, "config", "spectuner", "spectuner.jsonc"))).toContain("spectuner-local")
    expect(existsSync(path.join(input().newHome, "config", "spectuner", "spectuner.json"))).toBe(false)
    expect(read(path.join(input().newHome, "data", "spectuner", "auth.json"))).toContain("openai")
    expect(read(path.join(input().userData, "spectuner.settings"))).toBe('{ "zoomLevel": 1.2 }\n')
    expect(stateRows(p.drafts)).toEqual({
      state: [
        { name: "opencode.global.dat", key: "model", value: '"anthropic/claude"' },
        { name: "opencode.window.w1.dat", key: "layout", value: "{}" },
        { name: "other.dat", key: "x", value: "1" },
        { name: "spectuner.global.dat", key: "model", value: '"anthropic/claude"' },
        { name: "spectuner.window.w1.dat", key: "layout", value: "{}" },
      ],
      document: [
        { key: "opencode.draft.abc.123.dat:prompt", value: "double the jump height" },
        { key: "spectuner.draft.abc.123.dat:prompt", value: "double the jump height" },
      ],
    })

    // Copied, not moved: the old files are all still there.
    for (const file of [p.database.from, ...p.files.filter((file) => !file.from.endsWith(".json")).map((file) => file.from)])
      expect(existsSync(file)).toBe(true)
    expect(existsSync(p.files[0].from)).toBe(true)
    // No temporary files left behind.
    expect(
      [path.dirname(p.database.to), path.dirname(p.files[2].to), input().userData].flatMap((dir) =>
        Array.from(new Bun.Glob("*.tmp").scanSync({ cwd: dir })),
      ),
    ).toEqual([])

    const marker = JSON.parse(read(p.marker))
    expect(marker.complete).toBe(true)
    expect((await SpecTunerUpgrade.run(input(), d)).status).toBe("already-done")
  })

  test("a partial earlier migration keeps what is already there and fills in the rest", async () => {
    oldInstall()
    const p = paths()
    // The new database exists (another run, or the new server, made it): it is never replaced.
    mkdirSync(path.dirname(p.database.to), { recursive: true })
    const fresh = new DatabaseSync(p.database.to)
    fresh.exec("CREATE TABLE credential (integration_id TEXT PRIMARY KEY, secret TEXT)")
    fresh.exec("INSERT INTO credential VALUES ('openai', 'sk-new')")
    fresh.close()
    write(p.files[3].to, '{ "zoomLevel": 2 }\n')
    // A renderer row already under its new name keeps its newer value.
    const drafts = new DatabaseSync(p.drafts)
    drafts.exec("INSERT INTO state VALUES ('spectuner.global.dat', 'model', '\"openai/gpt\"', 2)")
    drafts.close()

    const report = await SpecTunerUpgrade.run(input(), deps().deps)
    expect(report.complete).toBe(true)
    expect(report.steps.find((step) => step.name === "database")?.result).toBe("kept")
    expect(report.steps.find((step) => step.name === "desktop settings")?.result).toBe("kept")
    expect(credentials(p.database.to)).toEqual([{ integration_id: "openai", secret: "sk-new" }])
    expect(read(p.files[3].to)).toBe('{ "zoomLevel": 2 }\n')
    expect(read(p.files[2].to)).toContain("spectuner-local")
    const rows = stateRows(p.drafts).state
    expect(rows.find((row) => row.name === "spectuner.global.dat")?.value).toBe('"openai/gpt"')
    expect(rows.find((row) => row.name === "spectuner.window.w1.dat")?.value).toBe("{}")
  })

  test("an earlier run that stopped after the log keeps the log and copies the database", async () => {
    const writer = oldInstall({ openWriter: true })
    const p = paths()

    try {
      write(p.database.to + "-wal", readFileSync(p.database.from + "-wal"))
      const report = await SpecTunerUpgrade.run(input(), deps().deps)
      expect(report.steps.find((step) => step.name === "database")?.result).toBe("copied")
    } finally {
      writer?.close()
    }

    expect(credentials(p.database.to)).toEqual([{ integration_id: "anthropic", secret: "sk-old" }])
  })

  test("a different log at the new location stops the database copy and replaces nothing", async () => {
    const writer = oldInstall({ openWriter: true })
    const p = paths()
    write(p.database.to + "-wal", "not the same log")

    try {
      const report = await SpecTunerUpgrade.run(input(), deps().deps)
      expect(report.complete).toBe(false)
      expect(report.steps.find((step) => step.name === "database")?.result).toBe("failed")
    } finally {
      writer?.close()
    }

    expect(existsSync(p.database.to)).toBe(false)
    expect(read(p.database.to + "-wal")).toBe("not the same log")
    // Incomplete: the next start tries again.
    expect(JSON.parse(read(p.marker)).complete).toBe(false)
  })

  test("the old service is identified by its executable, stopped, and then the database is copied", async () => {
    oldInstall()
    const service = await oldService("pw")
    register({ url: service.url, pid: service.child.pid, password: "pw" })
    const { deps: d, calls } = deps({ imagePath: async (pid) => (pid === service.child.pid ? oldExe() : undefined) })

    const report = await SpecTunerUpgrade.run(input(), d)
    expect(report.steps[0]).toMatchObject({ name: "old service", result: "stopped" })
    expect(calls.stop).toEqual([paths().registration])
    await service.child.exited
    expect(alive(service.child.pid)).toBe(false)
    // stop() removes the registration it acted on; the rest of the old folder stays.
    expect(existsSync(paths().registration)).toBe(false)
    expect(existsSync(paths().database.from)).toBe(true)
    expect(report.steps.find((step) => step.name === "database")?.result).toBe("copied")
    expect(report.complete).toBe(true)
  })

  test("without its executable path, the old service is identified by its own answer", async () => {
    oldInstall()
    const service = await oldService("pw")
    register({ url: service.url, pid: service.child.pid, password: "pw" })
    const { deps: d, calls } = deps()

    const report = await SpecTunerUpgrade.run(input(), d)
    expect(calls.infoPid).toEqual([service.child.pid])
    expect(report.steps[0]).toMatchObject({ name: "old service", result: "stopped" })
    await service.child.exited
    expect(report.complete).toBe(true)
  })

  test("a service that cannot be confirmed is left running and the database is not copied", async () => {
    oldInstall()
    const service = await oldService("pw")
    register({ url: service.url, pid: service.child.pid, password: "wrong" })
    const { deps: d, calls } = deps()

    const report = await SpecTunerUpgrade.run(input(), d)
    expect(report.steps[0]).toMatchObject({ name: "old service", result: "failed" })
    expect(calls.stop).toEqual([])
    expect(alive(service.child.pid)).toBe(true)
    expect(existsSync(paths().registration)).toBe(true)
    expect(report.steps.find((step) => step.name === "database")?.result).toBe("skipped")
    expect(existsSync(paths().database.to)).toBe(false)
    // The rest still carries over, and the app is not stopped from starting.
    expect(report.steps.find((step) => step.name === "desktop settings")?.result).toBe("copied")
    expect(report.complete).toBe(false)
  })

  test("a registration naming another application's process is left alone", async () => {
    oldInstall()
    // Answers like the old server, but runs from an installed OpenCode's folders.
    const service = await oldService("pw")
    register({ url: service.url, pid: service.child.pid, password: "pw" })
    const opencode = path.join(root.dir, "Roaming", "ai.opencode.desktop", "cli", "1.0.0-x64", "opencode-cli.exe")
    const { deps: d, calls } = deps({ imagePath: async () => opencode })

    const report = await SpecTunerUpgrade.run(input(), d)
    expect(report.steps[0]).toMatchObject({ name: "old service", result: "skipped" })
    expect(calls.stop).toEqual([])
    expect(calls.infoPid).toEqual([])
    expect(alive(service.child.pid)).toBe(true)
    expect(existsSync(paths().registration)).toBe(true)
    // The process with that id is not the old service, so the old service is not running: the database is safe.
    expect(report.steps.find((step) => step.name === "database")?.result).toBe("copied")
    expect(report.complete).toBe(true)
  })

  test.skipIf(process.platform !== "win32")("the real process lookup does not take another program for the old service", async () => {
    oldInstall()
    const service = await oldService("pw")
    register({ url: service.url, pid: service.child.pid, password: "pw" })
    const { deps: d, calls } = deps({ imagePath: SpecTunerUpgrade.windowsImagePath })

    const report = await SpecTunerUpgrade.run(input(), d)
    expect(report.steps[0]).toMatchObject({ name: "old service", result: "skipped" })
    expect(calls.stop).toEqual([])
    expect(alive(service.child.pid)).toBe(true)
  }, 30_000)

  test("a stale registration whose process is gone is not acted on", async () => {
    oldInstall()
    const gone = Bun.spawn([process.execPath, "-e", "0"])
    await gone.exited
    register({ url: "http://127.0.0.1:9", pid: gone.pid, password: "pw" })
    const { deps: d, calls } = deps({ alive: (pid) => pid !== gone.pid && alive(pid) })

    const report = await SpecTunerUpgrade.run(input(), d)
    expect(report.steps[0]).toMatchObject({ name: "old service", result: "skipped" })
    expect(calls.stop).toEqual([])
    expect(calls.imagePath).toEqual([])
    expect(report.steps.find((step) => step.name === "database")?.result).toBe("copied")
  })

  test("failures are recorded and never thrown", async () => {
    oldInstall()
    write(paths().drafts, "not a database")
    const { deps: d } = deps()

    const report = await SpecTunerUpgrade.run(input(), d)
    expect(report.steps.find((step) => step.name === "renderer state")?.result).toBe("failed")
    expect(report.steps.find((step) => step.name === "database")?.result).toBe("copied")
    expect(report.complete).toBe(false)
  })

  test("only opencode-cli.exe inside SpecTuner's own folders is the old server", () => {
    const dirs = ["C:\\Users\\p\\AppData\\Roaming\\com.spectuner.desktop\\cli"]
    const old = "C:\\Users\\p\\AppData\\Roaming\\com.spectuner.desktop\\cli\\2.3.0-x64\\opencode-cli.exe"
    expect(SpecTunerUpgrade.isOldServer(old, dirs)).toBe(true)
    expect(SpecTunerUpgrade.isOldServer(old.toUpperCase(), dirs)).toBe(true)
    expect(SpecTunerUpgrade.isOldServer(old.replace("opencode-cli", "spectuner-server"), dirs)).toBe(false)
    expect(
      SpecTunerUpgrade.isOldServer("C:\\Users\\p\\AppData\\Roaming\\ai.opencode.desktop\\cli\\1\\opencode-cli.exe", dirs),
    ).toBe(false)
    expect(
      SpecTunerUpgrade.isOldServer("C:\\Users\\p\\AppData\\Roaming\\com.spectuner.desktop\\cli-other\\opencode-cli.exe", dirs),
    ).toBe(false)
    expect(SpecTunerUpgrade.isOldServer("C:\\Users\\p\\.opencode\\bin\\opencode.exe", dirs)).toBe(false)
  })
})

test("the upgrade step applies only on Windows, where the builds with opencode's names shipped", async () => {
  expect(SpecTunerUpgrade.applies("win32")).toBe(true)
  expect(SpecTunerUpgrade.applies("darwin")).toBe(false)
  expect(SpecTunerUpgrade.applies("linux")).toBe(false)
  // The app's entry point checks it before doing anything.
  const source = await Bun.file(path.join(import.meta.dirname, "index.ts")).text()
  const step = source.slice(source.indexOf("export async function upgradeFromPreviousBuild"))
  expect(step.slice(0, step.indexOf("const newHome"))).toContain("SpecTunerUpgrade.applies(process.platform)")
})
