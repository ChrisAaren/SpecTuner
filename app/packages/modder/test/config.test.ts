import { describe, expect, test } from "bun:test"
import { ConfigFlag } from "@spectuner/schema/config/flag"
import { readFileSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { SpecTunerConfig } from "../src/config.ts"
import { SpecTunerEnv } from "../src/env.ts"

const pluginDir = path.resolve(import.meta.dir, "..", "plugin")

describe("SpecTunerConfig.document", () => {
  const doc = SpecTunerConfig.document({ pluginDir })

  test("the modder is the default primary agent with the bundled prompt", () => {
    expect(doc.default_agent).toBe("modder")
    expect(doc.agents.modder.mode).toBe("primary")
    expect(doc.agents.modder.system).toBe(
      readFileSync(path.join(import.meta.dir, "..", "src", "prompt", "modder.txt"), "utf8"),
    )
    expect(doc.agents.modder.system).toContain("Begin every new request with game_detect")
    expect(doc.agents.modder.steps).toBeGreaterThan(0)
  })

  test("modder permissions deny everything first, then allow exactly the ten game tools", () => {
    const rules: readonly unknown[] = doc.agents.modder.permissions
    expect(rules[0]).toEqual({ action: "*", resource: "*", effect: "deny" })
    expect(rules.slice(1)).toEqual(
      [
        "game_detect",
        "game_benchmark",
        "game_set_quality",
        "game_changes",
        "game_revert",
        "game_search_code",
        "game_read_code",
        "game_build_mod",
        "game_read_logs",
        "game_reapply",
      ].map((action) => ({ action, resource: "*", effect: "allow" })),
    )
  })

  test("a policy denies every skill, so skills from the player's home folder never reach the model", () => {
    expect(doc.policies).toEqual([{ action: "skill.use", resource: "*", effect: "deny" }])
  })

  test("global permissions deny everything, so hidden helper agents get no tools", () => {
    expect(doc.permissions).toEqual([{ action: "*", resource: "*", effect: "deny" }])
  })

  test("built-in selectable agents are disabled and the hidden server agents are left alone", () => {
    const agents: Record<string, unknown> = doc.agents
    for (const id of ["build", "plan", "general", "explore"]) expect(agents[id]).toEqual({ disabled: true })
    for (const id of ["title", "compaction", "summary"]) expect(agents[id]).toBeUndefined()
  })

  test("plugins load the absolute plugin dir, then remove built-in tools and MCP config loading and keep input repair", () => {
    expect(path.isAbsolute(doc.plugins[0])).toBe(true)
    expect(doc.plugins).toEqual([
      pluginDir,
      "-spectuner.tool.*",
      "-spectuner.tools",
      "-spectuner.tools.*",
      "-spectuner.browser",
      "-spectuner.config.mcp",
      "-spectuner.mcp.codemode.defaults",
      "spectuner.tool.input.repair",
    ])
  })

  test("a relative plugin dir is refused", () => {
    expect(() => SpecTunerConfig.document({ pluginDir: "plugin" })).toThrow(/must be absolute/)
  })

  test("names no provider or model", () => {
    const text = JSON.stringify(doc)
    expect(Object.keys(doc)).not.toContain("model")
    expect(Object.keys(doc)).not.toContain("providers")
    expect(text).not.toMatch(/"(model|providers|provider|small_model)"/)
  })
})

describe("SpecTunerEnv.make", () => {
  const home = path.resolve("Z:\\spectuner-home")
  const sidecar = path.resolve("Z:\\app\\spectuner-sidecar.exe")
  // Absolute on the system the tests run on, like `home` and `sidecar`: the server's paths are always the host's.
  const localAppData = path.resolve("C:\\Users\\player\\AppData\\Local")
  const base = {
    PATH: "C:\\Windows",
    LOCALAPPDATA: localAppData,
    XDG_DATA_HOME: "C:\\Users\\player\\.local\\share",
    XDG_CONFIG_HOME: "C:\\Users\\player\\.config",
    xdg_state_home: "C:\\Users\\player\\.local\\state",
    SPECTUNER_CONFIG_DIR: "C:\\Users\\player\\.config\\spectuner",
    SPECTUNER_CONFIG: "C:\\Users\\player\\spectuner.json",
    SPECTUNER_DB: "C:\\Users\\player\\.local\\share\\spectuner\\spectuner.db",
    SPECTUNER_FLAGS: "all",
    SPECTUNER_PASSWORD: "leaked",
    SPECTUNER_HOME: "Z:\\state",
  }
  const env = SpecTunerEnv.make({ home, sidecar, pluginDir, base })

  test("puts every XDG dir under home", () => {
    expect(env.XDG_DATA_HOME).toBe(path.join(home, "data"))
    expect(env.XDG_CONFIG_HOME).toBe(path.join(home, "config"))
    expect(env.XDG_CACHE_HOME).toBe(path.join(home, "cache"))
    expect(env.XDG_STATE_HOME).toBe(path.join(home, "state"))
  })

  test("drops the user's own server and XDG settings but keeps the rest", () => {
    const keys = Object.keys(env)
    expect(keys.filter((key) => /^xdg_/i.test(key)).sort()).toEqual([
      "XDG_CACHE_HOME",
      "XDG_CONFIG_HOME",
      "XDG_DATA_HOME",
      "XDG_STATE_HOME",
    ])
    for (const key of ["SPECTUNER_CONFIG_DIR", "SPECTUNER_CONFIG", "SPECTUNER_DB", "SPECTUNER_PASSWORD"])
      expect(keys).not.toContain(key)
    expect(Object.values(env).some((value) => value.includes("C:\\Users\\player\\."))).toBe(false)
    expect(env.PATH).toBe("C:\\Windows")
    expect(env.SPECTUNER_HOME).toBe("Z:\\state")
  })

  test("sets the lock-down variables", () => {
    expect(env.SPECTUNER_DISABLE_PROJECT_CONFIG).toBe("1")
    expect(env.SPECTUNER_DISABLE_CLAUDE_CODE).toBe("1")
    expect(env.SPECTUNER_DISABLE_FREE_MODELS).toBe("1")
    expect(env.SPECTUNER_LOCKDOWN).toBe("1")
    expect(env.SPECTUNER_SIDECAR).toBe(sidecar)
    expect(JSON.parse(env.SPECTUNER_CONFIG_CONTENT)).toEqual(SpecTunerConfig.document({ pluginDir }))
  })

  test("pins every experimental flag off", () => {
    expect(env.SPECTUNER_FLAGS.split(",").sort()).toEqual(
      [
        "artifacts",
        "console_watches",
        "full_codemode",
        "home_memory",
        "machines",
        "mcp_events",
        "media_blob",
        "project_overlay",
        "recommended_api",
        "sqlite_fts",
        "todo",
        "video_generate",
      ].map((flag) => `-${flag}`),
    )
  })

  test("covers every flag the server knows, so an upstream flag added later is caught here", () => {
    const pinned: string[] = [...SpecTunerEnv.FLAGS]
    expect(pinned.sort()).toEqual(Object.keys(ConfigFlag.registry).sort())
  })

  test("defaults home to %LOCALAPPDATA%\\SpecTuner\\app", () => {
    const defaulted = SpecTunerEnv.make({ sidecar, pluginDir, base, platform: "win32" })
    expect(defaulted.XDG_DATA_HOME).toBe(path.join(localAppData, "SpecTuner", "app", "data"))
  })

  test.each([
    { platform: "win32" as const, folder: () => path.join(localAppData, "SpecTuner") },
    { platform: "darwin" as const, folder: () => path.join(os.homedir(), "Library", "Application Support", "SpecTuner") },
    // A relative XDG_DATA_HOME is ignored, as the XDG specification says.
    { platform: "linux" as const, folder: () => path.join(os.homedir(), ".local", "share", "SpecTuner") },
  ])("$platform: one SpecTuner folder holds the server's home and the sidecar's state", ({ platform, folder }) => {
    const { SPECTUNER_HOME: _home, ...rest } = base
    const own = { ...rest, XDG_DATA_HOME: "relative" }
    const made = SpecTunerEnv.make({ sidecar, pluginDir, base: own, platform })
    expect(made.XDG_DATA_HOME).toBe(path.join(folder(), "app", "data"))
    // Always set, so the sidecar never derives its folder from the server's XDG_DATA_HOME.
    expect(made.SPECTUNER_HOME).toBe(folder())
    expect(SpecTunerEnv.folder(own, platform)).toBe(folder())
    expect(SpecTunerEnv.defaultHome(own, platform)).toBe(path.join(folder(), "app"))
  })

  test("linux: the SpecTuner folder follows an absolute XDG_DATA_HOME", () => {
    const data = path.resolve("/data/player")
    expect(SpecTunerEnv.folder({ XDG_DATA_HOME: data }, "linux")).toBe(path.join(data, "SpecTuner"))
  })

  test("an inherited SPECTUNER_HOME is kept, in any letter case", () => {
    expect(env.SPECTUNER_HOME).toBe("Z:\\state")
    const { SPECTUNER_HOME: _home, ...rest } = base
    const lower = SpecTunerEnv.make({ sidecar, pluginDir, base: { ...rest, spectuner_home: "Z:\\other" }, platform: "win32" })
    expect(Object.entries(lower).filter(([key]) => /^spectuner_home$/i.test(key))).toEqual([["spectuner_home", "Z:\\other"]])
  })

  test("the sidecar's file name and published location per system", () => {
    expect(SpecTunerEnv.sidecarFile("win32")).toBe("spectuner-sidecar.exe")
    expect(SpecTunerEnv.sidecarFile("linux")).toBe("spectuner-sidecar")
    expect(SpecTunerEnv.sidecarFile("darwin")).toBe("spectuner-sidecar")
    expect(SpecTunerEnv.sidecarRid("win32", "x64")).toBe("win-x64")
    expect(SpecTunerEnv.sidecarRid("linux", "x64")).toBe("linux-x64")
    expect(SpecTunerEnv.sidecarRid("darwin", "arm64")).toBe("osx-arm64")
    const artifacts = path.resolve("artifacts")
    expect(SpecTunerEnv.publishedSidecar(artifacts, "win32", "x64")).toBe(path.join(artifacts, "sidecar", "spectuner-sidecar.exe"))
    expect(SpecTunerEnv.publishedSidecar(artifacts, "linux", "x64")).toBe(path.join(artifacts, "sidecar", "linux-x64", "spectuner-sidecar"))
    expect(SpecTunerEnv.publishedSidecar(artifacts, "darwin", "arm64")).toBe(path.join(artifacts, "sidecar", "osx-arm64", "spectuner-sidecar"))
  })

  test("refuses relative paths", () => {
    expect(() => SpecTunerEnv.make({ home: "rel", sidecar, pluginDir, base })).toThrow(/home must be absolute/)
    expect(() => SpecTunerEnv.make({ home, sidecar: "sidecar.exe", pluginDir, base })).toThrow(
      /sidecar must be absolute/,
    )
  })
})
