import { afterEach, beforeAll, describe, expect, test } from "bun:test"
import { mkdtempSync, rmSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { Sidecar } from "../plugin/sidecar.ts"
import { SpecTunerTools } from "../plugin/tools.ts"
import { SpecTunerConfig } from "../src/config.ts"
import { calls, fakeSidecar } from "./fixture/sidecar.ts"

const state = { exe: "", dir: "" }
const saved = { log: process.env.FAKE_SIDECAR_LOG, mode: process.env.FAKE_SIDECAR_MODE }

beforeAll(async () => {
  state.exe = await fakeSidecar()
}, 120_000)

afterEach(() => {
  process.env.FAKE_SIDECAR_LOG = saved.log
  process.env.FAKE_SIDECAR_MODE = saved.mode
  if (state.dir) rmSync(state.dir, { recursive: true, force: true })
  state.dir = ""
})

/** A fresh call log for the fake sidecar, which inherits this process's environment. */
function useLog(mode?: string) {
  state.dir = mkdtempSync(path.join(os.tmpdir(), "spectuner-sidecar-test-"))
  const log = path.join(state.dir, "calls.jsonl")
  process.env.FAKE_SIDECAR_LOG = log
  if (mode) process.env.FAKE_SIDECAR_MODE = mode
  if (!mode) delete process.env.FAKE_SIDECAR_MODE
  return log
}

function alive(pid: number) {
  try {
    process.kill(pid, 0)
    return true
  } catch {
    return false
  }
}

describe("Sidecar.run", () => {
  test("writes the request JSON to stdin and parses the envelope", async () => {
    const log = useLog()
    const envelope = await Sidecar.run({
      exe: state.exe,
      command: "detect",
      request: { gamePath: "C:\\Games\\Fake Game" },
      timeoutMs: 60_000,
    })
    expect(envelope.ok).toBe(true)
    expect(envelope.command).toBe("detect")
    expect(envelope.data).toMatchObject({ supported: true, game: { name: "Fake Game" } })
    const logged = await calls(log)
    expect(logged.map((call) => [call.command, call.request])).toEqual([
      ["detect", { gamePath: "C:\\Games\\Fake Game" }],
    ])
  })

  test("returns ok:false envelopes instead of throwing", async () => {
    useLog()
    const envelope = await Sidecar.run({
      exe: state.exe,
      command: "set-quality",
      request: { gamePath: "x" },
      timeoutMs: 60_000,
    })
    expect(envelope.ok).toBe(false)
    expect(envelope.error).toEqual({
      code: "UNKNOWN_SETTING",
      message: "One of those settings does not exist.",
      detail: "bogus",
    })
  })

  test("throws a readable error when the helper prints no envelope", async () => {
    useLog("garbage")
    const run = Sidecar.run({ exe: state.exe, command: "detect", request: { gamePath: "x" }, timeoutMs: 60_000 })
    expect(run).rejects.toThrow(/exit code 3.*fake sidecar crashed on purpose/s)
    await run.catch(() => {})
  })

  test("throws a clear error when the exe is not configured or missing", () => {
    const request = { command: "detect", request: {}, timeoutMs: 1000 }
    expect(() => Sidecar.run({ ...request, exe: undefined })).toThrow(/not configured \(SPECTUNER_SIDECAR is not set\)/)
    const missing = path.join(os.tmpdir(), "spectuner-does-not-exist", "spectuner-sidecar.exe")
    expect(() => Sidecar.run({ ...request, exe: missing })).toThrow(/was not found at/)
    expect(() => Sidecar.run({ ...request, exe: "relative\\sidecar.exe" })).toThrow(/was not found at/)
  })

  test("abort kills the child", async () => {
    const log = useLog("hang")
    const controller = new AbortController()
    const run = Sidecar.run({
      exe: state.exe,
      command: "launch-and-benchmark",
      request: { gamePath: "x" },
      timeoutMs: 60_000,
      signal: controller.signal,
    })
    const started = await waitFor(async () => (await calls(log))[0])
    expect(alive(started.pid)).toBe(true)
    controller.abort()
    expect(run).rejects.toThrow(/Stopped by the player/)
    await run.catch(() => {})
    await waitFor(() => !alive(started.pid))
    expect(alive(started.pid)).toBe(false)
  })

  test("timeout kills the child", async () => {
    const log = useLog("hang")
    const run = Sidecar.run({ exe: state.exe, command: "detect", request: { gamePath: "x" }, timeoutMs: 1500 })
    expect(run).rejects.toThrow(/did not finish within 2 seconds/)
    await run.catch(() => {})
    const started = (await calls(log))[0]
    await waitFor(() => !alive(started.pid))
    expect(alive(started.pid)).toBe(false)
  })

  test("format drops the command field", () => {
    expect(Sidecar.format({ ok: true, command: "detect", summary: "s", data: { a: 1 } })).toBe(
      '{"ok":true,"summary":"s","data":{"a":1}}',
    )
  })
})

describe("SpecTunerTools", () => {
  const signal = new AbortController().signal
  const tools = (exe?: string) => SpecTunerTools.make("D:\\Games\\My Game", () => exe ?? state.exe)
  const find = (name: string, exe?: string) => {
    const tool = tools(exe).find((item) => item.name === name)
    if (!tool) throw new Error(`missing tool ${name}`)
    return tool
  }

  test("names match the config allow-list and every tool is direct (not behind Code Mode)", () => {
    expect(tools().map((tool) => tool.name)).toEqual([...SpecTunerTools.names])
    expect([...SpecTunerTools.names]).toEqual([...SpecTunerConfig.TOOLS])
    expect(tools().every((tool) => tool.options?.codemode === false)).toBe(true)
  })

  test("every request gets gamePath from the session folder, never from the model", async () => {
    const log = useLog()
    await find("game_revert").execute({ changeId: "c0003", gamePath: "C:\\Elsewhere" }, { signal } as never)
    await find("game_detect").execute({}, { signal } as never)
    const logged = await calls(log)
    expect(logged.map((call) => [call.command, call.request])).toEqual([
      ["revert", { changeId: "c0003", gamePath: "D:\\Games\\My Game" }],
      ["detect", { gamePath: "D:\\Games\\My Game" }],
    ])
  })

  test("maps each tool to its sidecar command", async () => {
    const log = useLog()
    const inputs: Record<string, unknown> = {
      game_detect: {},
      game_benchmark: { label: "before", measureSeconds: 5 },
      game_set_quality: { title: "t", description: "d", settings: { shadowDistance: 40 } },
      game_changes: {},
      game_revert: { all: true },
      game_search_code: { query: "jump", kind: "name", limit: 5 },
      game_read_code: { type: "Player", member: "Jump" },
      game_build_mod: { name: "DoubleJump", title: "t", description: "d", source: "class A {}" },
      game_read_logs: { source: "player", filter: "DoubleJump", tail: 10 },
      game_reapply: { changeId: "c0004" },
    }
    await Promise.all(SpecTunerTools.names.map((name) => find(name).execute(inputs[name], { signal } as never)))
    const logged = Object.fromEntries((await calls(log)).map((call) => [call.command, call.request]))
    expect(logged).toEqual({
      detect: { gamePath: "D:\\Games\\My Game" },
      "launch-and-benchmark": { label: "before", measureSeconds: 5, gamePath: "D:\\Games\\My Game" },
      "set-quality": { title: "t", description: "d", settings: { shadowDistance: 40 }, gamePath: "D:\\Games\\My Game" },
      changes: { gamePath: "D:\\Games\\My Game" },
      revert: { all: true, gamePath: "D:\\Games\\My Game" },
      "search-code": { query: "jump", kind: "name", limit: 5, gamePath: "D:\\Games\\My Game" },
      "read-code": { type: "Player", member: "Jump", gamePath: "D:\\Games\\My Game" },
      "build-plugin": {
        name: "DoubleJump",
        title: "t",
        description: "d",
        source: "class A {}",
        gamePath: "D:\\Games\\My Game",
      },
      "read-logs": { source: "player", filter: "DoubleJump", tail: 10, gamePath: "D:\\Games\\My Game" },
      reapply: { changeId: "c0004", gamePath: "D:\\Games\\My Game" },
    })
  })

  test("an ok:false envelope is returned as content the model can read", async () => {
    useLog()
    const result = await find("game_set_quality").execute({ title: "t", description: "d", settings: { bogus: 1 } }, {
      signal,
    } as never)
    expect(JSON.parse(String(result.content))).toEqual({
      ok: false,
      error: { code: "UNKNOWN_SETTING", message: "One of those settings does not exist.", detail: "bogus" },
      data: { valid: ["shadowDistance"] },
    })
    expect(result.metadata).toEqual({
      title: "One of those settings does not exist.",
      ok: false,
      code: "UNKNOWN_SETTING",
    })
  })

  test("a missing sidecar fails the call with a clear message", async () => {
    const missing = path.join(os.tmpdir(), "spectuner-does-not-exist", "spectuner-sidecar.exe")
    expect(find("game_detect", missing).execute({}, { signal } as never)).rejects.toThrow(
      /helper program was not found/,
    )
  })
})

async function waitFor<T>(check: () => T | undefined | false | Promise<T | undefined | false>, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const value = await check()
    if (value) return value
    await Bun.sleep(25)
  }
  throw new Error(`condition not met within ${timeoutMs}ms`)
}
