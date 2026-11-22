import { afterAll, beforeAll, describe, expect, test } from "bun:test"
import { mkdtemp, readFile, rm, stat } from "node:fs/promises"
import os from "node:os"
import path from "node:path"
import { fallback } from "@spectuner/client/service"
import { SpecTunerServerEnvironment } from "./server-environment"

// An installed OpenCode's service listens here on Windows (0x70DE); SpecTuner must never.
const OPENCODE_PORT = 28894

const root = { dir: "" }

beforeAll(async () => {
  root.dir = await mkdtemp(path.join(os.tmpdir(), "spectuner-env-"))
  await Bun.write(path.join(root.dir, "resources", "modder.txt"), "You are the modder.")
})

afterAll(() => rm(root.dir, { recursive: true, force: true }))

const local = () => path.join(root.dir, "Local")
const resources = () => path.join(root.dir, "resources")
const server = (input: Partial<SpecTunerServerEnvironment.Input> = {}) =>
  SpecTunerServerEnvironment.resolve(
    // The Windows layout unless a test names another system, so these cases check Windows on every runner.
    { packaged: true, resources: resources(), platform: "win32", ...input },
    { LOCALAPPDATA: local(), USERPROFILE: path.join(root.dir, "home") },
  )

// What a developer machine with OpenCode installed may have in the environment the app starts with.
const developer = () => ({
  PATH: "C:\\Windows",
  USERPROFILE: path.join(root.dir, "home"),
  SPECTUNER_CONFIG_DIR: path.join(root.dir, "home", ".config", "spectuner"),
  SPECTUNER_DB: path.join(root.dir, "home", ".local", "share", "spectuner", "spectuner.db"),
  SPECTUNER_CONFIG_CONTENT: "{}",
  SPECTUNER_FLAGS: "all",
  XDG_STATE_HOME: path.join(root.dir, "home", ".local", "state"),
  xdg_data_home: path.join(root.dir, "home", ".local", "share"),
  SPECTUNER_DESKTOP_REUSE_SERVER: "1",
  SPECTUNER_DESKTOP_REUSE_SERVICE_FILE: path.join(root.dir, "home", ".local", "state", "spectuner", "service.json"),
  SPECTUNER_DESKTOP_ISOLATED_SERVER: "1",
  SPECTUNER_DESKTOP_SERVER_CHANNEL: "local",
  SPECTUNER_DESKTOP_CLI_DEV: "packages/cli",
  // Set by the desktop itself for its server, or read by the desktop only.
  SPECTUNER_CLIENT: "desktop",
  SPECTUNER_EXPERIMENTAL_FILEWATCHER: "true",
  SPECTUNER_DESKTOP_REMOTE_DEBUGGING_PORT: "9222",
})

describe("SpecTuner server environment", () => {
  test.each([
    { name: "packaged", input: { packaged: true }, home: () => path.join(local(), "SpecTuner", "app"), port: 0x7354 },
    { name: "unpackaged", input: { packaged: false }, home: () => path.join(local(), "SpecTuner", "app-dev"), port: 0x7355 },
    {
      name: "test root",
      input: { packaged: true, testRoot: path.join(os.tmpdir(), "desktop-test-root") },
      home: () => path.join(os.tmpdir(), "desktop-test-root"),
      port: 0x7354,
    },
  ])("$name: own home, own port, resources from the app", ({ input, home, port }) => {
    const resolved = server(input)
    expect(resolved.home).toBe(home())
    expect(resolved.port).toBe(port)
    expect(resolved.port).not.toBe(OPENCODE_PORT)
    expect(resolved.sidecar).toBe(path.join(resources(), "spectuner-sidecar.exe"))
    expect(resolved.pluginDir).toBe(path.join(resources(), "plugin"))
    expect(resolved.system).toBe("You are the modder.")
  })

  test("the server gets SpecTuner's folders, config and sidecar, and nothing of an installed OpenCode", () => {
    const resolved = server()
    const env: NodeJS.ProcessEnv = developer()
    SpecTunerServerEnvironment.apply(env, resolved, "win32")

    expect(env.XDG_DATA_HOME).toBe(path.join(resolved.home, "data"))
    expect(env.XDG_CONFIG_HOME).toBe(path.join(resolved.home, "config"))
    expect(env.XDG_CACHE_HOME).toBe(path.join(resolved.home, "cache"))
    expect(env.XDG_STATE_HOME).toBe(path.join(resolved.home, "state"))
    expect(env.SPECTUNER_SIDECAR).toBe(resolved.sidecar)
    expect(env.SPECTUNER_DISABLE_PROJECT_CONFIG).toBe("1")
    expect(env.SPECTUNER_DISABLE_CLAUDE_CODE).toBe("1")
    expect(env.SPECTUNER_DISABLE_AUTOUPDATE).toBe("1")
    // The server refuses every route the app never calls (packages/server/src/middleware/lockdown.ts).
    expect(env.SPECTUNER_LOCKDOWN).toBe("1")
    const config = JSON.parse(env.SPECTUNER_CONFIG_CONTENT ?? "")
    expect(config.default_agent).toBe("modder")
    expect(config.agents.modder.system).toBe("You are the modder.")
    expect(config.plugins[0]).toBe(resolved.pluginDir)

    // The developer's own server settings and the desktop's server-selection switches are gone.
    for (const key of [
      "SPECTUNER_CONFIG_DIR",
      "SPECTUNER_DB",
      "xdg_data_home",
      "SPECTUNER_DESKTOP_REUSE_SERVER",
      "SPECTUNER_DESKTOP_REUSE_SERVICE_FILE",
      "SPECTUNER_DESKTOP_ISOLATED_SERVER",
      "SPECTUNER_DESKTOP_SERVER_CHANNEL",
      "SPECTUNER_DESKTOP_CLI_DEV",
    ])
      expect(env[key], key).toBeUndefined()
    expect(env.SPECTUNER_FLAGS).not.toContain("all")
    expect(Object.values(env).filter((value) => value?.includes(path.join(root.dir, "home")))).toEqual([
      path.join(root.dir, "home"),
    ])

    // Kept: the desktop's own settings for its server, and switches the CLI never reads.
    expect(env.PATH).toBe("C:\\Windows")
    expect(env.SPECTUNER_CLIENT).toBe("desktop")
    expect(env.SPECTUNER_EXPERIMENTAL_FILEWATCHER).toBe("true")
    expect(env.SPECTUNER_DESKTOP_REMOTE_DEBUGGING_PORT).toBe("9222")
  })

  test("applying again restores what upstream startup removed", () => {
    const resolved = server()
    const env: NodeJS.ProcessEnv = developer()
    SpecTunerServerEnvironment.apply(env, resolved, "win32")
    const first = { ...env }
    // preferApplicationEnvironment deletes XDG_STATE_HOME on Windows; a login shell may bring its own SPECTUNER_*.
    delete env.XDG_STATE_HOME
    env.SPECTUNER_CONFIG_DIR = path.join(root.dir, "home", ".config", "spectuner")
    SpecTunerServerEnvironment.apply(env, resolved, "win32")
    expect(env).toEqual(first)
  })

  test("the client looks for the service registration in SpecTuner's state folder", () => {
    const resolved = server()
    const saved = { ...process.env }

    try {
      process.env.XDG_STATE_HOME = path.join(root.dir, "home", ".local", "state")
      SpecTunerServerEnvironment.apply(process.env, resolved, "win32")
      expect(fallback()).toBe(path.join(resolved.home, "state", "spectuner", "service.json"))
    } finally {
      Object.keys(process.env).forEach((key) => delete process.env[key])
      Object.assign(process.env, saved)
    }
  })

  test("on Windows the server needs nothing extra at spawn time and is found through the environment", () => {
    const resolved = server()
    expect(SpecTunerServerEnvironment.spawnEnv(developer(), resolved, "win32")).toBeUndefined()
    expect(SpecTunerServerEnvironment.registrationFile(resolved, "win32")).toBeUndefined()
  })

  test("the service port is pinned in the service's own config, keeping its password", async () => {
    const resolved = server({ testRoot: path.join(root.dir, "pin") })
    const file = path.join(resolved.home, "config", "spectuner", "service.json")

    SpecTunerServerEnvironment.pinPort(resolved)
    expect(await Bun.file(file).json()).toEqual({ port: 0x7354 })

    await Bun.write(file, JSON.stringify({ port: OPENCODE_PORT, password: "secret" }))
    SpecTunerServerEnvironment.pinPort(resolved)
    expect(await Bun.file(file).json()).toEqual({ port: 0x7354, password: "secret" })

    // Already pinned: the service's file is left as it is.
    const before = (await stat(file)).mtimeMs
    const text = await readFile(file, "utf8")
    SpecTunerServerEnvironment.pinPort(resolved)
    expect(await readFile(file, "utf8")).toBe(text)
    expect((await stat(file)).mtimeMs).toBe(before)
  })
})

describe("SpecTuner server environment on macOS and Linux", () => {
  const home = () => path.join(root.dir, "user")
  const resolveOn = (platform: NodeJS.Platform, base: Record<string, string> = {}, packaged = true) =>
    SpecTunerServerEnvironment.resolve({ packaged, resources: resources(), platform }, base)

  test.each([
    { platform: "darwin" as const, base: {}, folder: () => path.join(os.homedir(), "Library", "Application Support", "SpecTuner") },
    { platform: "linux" as const, base: { XDG_DATA_HOME: "<root>/xdg-data" }, folder: () => path.join(root.dir, "xdg-data", "SpecTuner") },
    { platform: "linux" as const, base: {}, folder: () => path.join(os.homedir(), ".local", "share", "SpecTuner") },
  ])("$platform: one SpecTuner folder, server under app and app-dev, sidecar without extension", ({ platform, base: raw, folder }) => {
    // The table is built before the temporary root exists.
    const base = Object.fromEntries(Object.entries(raw).map(([key, value]) => [key, value.replace("<root>", root.dir)]))
    expect(resolveOn(platform, base).home).toBe(path.join(folder(), "app"))
    expect(resolveOn(platform, base, false).home).toBe(path.join(folder(), "app-dev"))
    expect(resolveOn(platform, base).sidecar).toBe(path.join(resources(), "spectuner-sidecar"))
  })

  test.each(["darwin", "linux"] as const)("%s: the app keeps its own XDG folders; the server gets SpecTuner's at spawn", (platform) => {
    const resolved = resolveOn(platform, { XDG_DATA_HOME: path.join(home(), ".local", "share") })
    const env: NodeJS.ProcessEnv = {
      PATH: "/usr/bin",
      HOME: home(),
      XDG_DATA_HOME: path.join(home(), ".local", "share"),
      XDG_CONFIG_HOME: path.join(home(), ".config"),
      XDG_RUNTIME_DIR: "/run/user/1000",
      xdg_state_home: path.join(home(), ".local", "state"),
      SPECTUNER_DB: path.join(home(), "spectuner.db"),
      SPECTUNER_DESKTOP_REUSE_SERVER: "1",
      SPECTUNER_CLIENT: "desktop",
    }
    SpecTunerServerEnvironment.apply(env, resolved, platform)

    // The player's own XDG variables are untouched: the system reads them in the app (xdg-open, file dialogs).
    expect(env.XDG_DATA_HOME).toBe(path.join(home(), ".local", "share"))
    expect(env.XDG_CONFIG_HOME).toBe(path.join(home(), ".config"))
    expect(env.XDG_RUNTIME_DIR).toBe("/run/user/1000")
    expect(env.xdg_state_home).toBe(path.join(home(), ".local", "state"))
    expect(env.XDG_STATE_HOME).toBeUndefined()
    expect(env.XDG_CACHE_HOME).toBeUndefined()
    // Everything else is applied as on Windows.
    expect(env.SPECTUNER_SIDECAR).toBe(resolved.sidecar)
    expect(env.SPECTUNER_LOCKDOWN).toBe("1")
    expect(env.SPECTUNER_HOME).toBe(path.dirname(resolved.home))
    expect(env.SPECTUNER_DB).toBeUndefined()
    expect(env.SPECTUNER_DESKTOP_REUSE_SERVER).toBeUndefined()
    expect(env.SPECTUNER_CLIENT).toBe("desktop")

    // Applying again changes nothing.
    const first = { ...env }
    SpecTunerServerEnvironment.apply(env, resolved, platform)
    expect(env).toEqual(first)

    // The server process gets SpecTuner's four folders and no other XDG variable.
    const spawn = SpecTunerServerEnvironment.spawnEnv(env, resolved, platform)
    expect(spawn).toEqual({
      XDG_RUNTIME_DIR: undefined,
      xdg_state_home: undefined,
      XDG_DATA_HOME: path.join(resolved.home, "data"),
      XDG_CONFIG_HOME: path.join(resolved.home, "config"),
      XDG_CACHE_HOME: path.join(resolved.home, "cache"),
      XDG_STATE_HOME: path.join(resolved.home, "state"),
    })
    const server = Object.fromEntries(Object.entries({ ...env, ...spawn }).filter(([, value]) => value !== undefined))
    expect(Object.keys(server).filter((key) => /^xdg_/i.test(key)).sort()).toEqual([
      "XDG_CACHE_HOME",
      "XDG_CONFIG_HOME",
      "XDG_DATA_HOME",
      "XDG_STATE_HOME",
    ])

    // The desktop finds the service where the server registers it, not in the player's state folder.
    expect(SpecTunerServerEnvironment.registrationFile(resolved, platform)).toBe(
      path.join(resolved.home, "state", "spectuner", "service.json"),
    )
  })
})
