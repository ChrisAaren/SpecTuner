import { afterAll, beforeAll, describe, expect, test } from "bun:test"
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { Scripted } from "../script/scripted.ts"
import { SpecTunerServer } from "../script/server.ts"
import { SpecTunerConfig } from "../src/config.ts"
import { calls, fakeSidecar } from "./fixture/sidecar.ts"

// The fake sidecar always runs. The real one runs too once it is published next to this checkout (or
// SPECTUNER_SIDECAR names it); SPECTUNER_TEST_GAME may name a real game folder for it.
const REAL = [process.env.SPECTUNER_SIDECAR, SpecTunerServer.DEFAULT_SIDECAR].find(
  (file): file is string => !!file && existsSync(file),
)
const COMPILED = path.join(SpecTunerServer.CLI_DIR, "dist", "cli-windows-x64", "bin", "spectuner.exe")
const GAME_TOOLS: string[] = [...SpecTunerConfig.TOOLS].sort()
const PROMPT = readFileSync(SpecTunerConfig.PROMPT_FILE, "utf8")

const state = { root: "", fake: "", log: "" }

beforeAll(async () => {
  state.root = mkdtempSync(path.join(os.tmpdir(), "spectuner-it-"))
  state.log = path.join(state.root, "sidecar-calls.jsonl")
  state.fake = await fakeSidecar()
}, 120_000)

afterAll(() => {
  if (state.root) rmSync(state.root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 })
})

/** An empty session folder named like a game. */
function fakeGame() {
  const dir = path.join(state.root, "Fake Game")
  mkdirSync(dir, { recursive: true })
  return dir
}

interface Check {
  readonly home: string
  readonly game: string
  readonly sidecar: string
  readonly exe?: string
  readonly pluginDir?: string
  readonly compact?: boolean
}

/** One scripted conversation (prompt, game_detect, final text) plus the checks every variant shares. */
async function detectRun(check: Check, assert: (envelope: Record<string, unknown>) => Promise<void>) {
  const run = await Scripted.run({
    ...check,
    prompt: "Check my game.",
    env: { FAKE_SIDECAR_LOG: state.log, SPECTUNER_HOME: path.join(state.root, "spectuner-home") },
  })
  try {
    // The only selectable agent is the modder; the server's own helpers stay hidden.
    const agents = await (await run.server.fetch(`/api/agent?${SpecTunerServer.location(check.game)}`)).json()
    const visible = agents.data
      .filter((agent: { hidden: boolean }) => !agent.hidden)
      .map((agent: { id: string }) => agent.id)
    expect(visible).toEqual(["modder"])

    // The modder answered, and its first model request offered exactly the ten game tools.
    expect(run.agents).toEqual(["modder"])
    expect(run.requests.length).toBe(2)
    expect((run.requests[0].tools ?? []).map((tool) => tool.function.name).sort()).toEqual(GAME_TOOLS)
    expect(run.requests[0].messages[0]).toMatchObject({ role: "system" })
    expect(String(run.requests[0].messages[0].content).startsWith(PROMPT.trim())).toBe(true)

    // The tool ran and its result went back to the model as the envelope without `command`.
    expect(run.tools.map((tool) => [tool.name, tool.state.status])).toEqual([["game_detect", "completed"]])
    const result = run.requests[1].messages.find((message) => message.role === "tool")
    const envelope = JSON.parse(String(result?.content))
    expect(typeof envelope.ok).toBe("boolean")
    expect(envelope.command).toBeUndefined()
    await assert(envelope)

    // The title was written by the same single model, and title requests carry no tools.
    expect(run.session.title).toBe("Game check")
    expect(run.titles.length).toBeGreaterThan(0)
    expect(run.titles.every((request) => (request.tools ?? []).length === 0)).toBe(true)

    // Compaction still works with the built-in tool plugins removed, and offers nothing beyond the game tools.
    if (check.compact) {
      expect(run.compactions.length).toBe(1)
      expect(run.types).toContain("compaction")
      const offered = (run.compactions[0].tools ?? []).map((tool) => tool.function.name)
      expect(offered.filter((name) => !GAME_TOOLS.includes(name))).toEqual([])
    }
  } finally {
    await run.stop()
    rmSync(state.log, { force: true })
  }
}

/** The fake sidecar logs its requests, so the test can see the exact gamePath it was sent. */
async function expectFakeDetect(game: string, envelope: Record<string, unknown>) {
  expect(envelope).toMatchObject({ ok: true, data: { supported: true, game: { name: path.basename(game) } } })
  expect((await calls(state.log)).map((call) => [call.command, call.request])).toEqual([["detect", { gamePath: game }]])
}

describe("SpecTuner server with a scripted model", () => {
  test("from source: the modder calls game_detect with the session folder, sees only game tools, and compaction works", async () => {
    const game = fakeGame()
    await detectRun(
      { home: path.join(state.root, "home-source"), game, sidecar: state.fake, compact: true },
      (envelope) => expectFakeDetect(game, envelope),
    )
  }, 180_000)

  // The packaged app runs the compiled CLI. This proves a copied plugin directory, with no node_modules anywhere near
  // it, loads there. Needs `bun run script/build.ts --single --skip-install` in packages/cli first.
  test.skipIf(!existsSync(COMPILED))(
    "compiled spectuner.exe loads a copied plugin directory with no node_modules",
    async () => {
      const game = fakeGame()
      const pluginDir = path.join(state.root, "plugin-copy")
      cpSync(SpecTunerServer.PLUGIN_DIR, pluginDir, { recursive: true })
      await detectRun(
        { home: path.join(state.root, "home-compiled"), game, sidecar: state.fake, exe: COMPILED, pluginDir },
        (envelope) => expectFakeDetect(game, envelope),
      )
    },
    180_000,
  )

  test("tool input is repaired or validated before the sidecar runs", async () => {
    const game = fakeGame()
    const replies: Scripted.Reply[] = [
      // Small models often send numbers as strings; the kept input-repair plugin turns this into 5.
      { tool: "game_benchmark", input: { label: "test", measureSeconds: "5" } },
      // Fails the schema (spaces in the name), so it must never reach the sidecar.
      { tool: "game_build_mod", input: { name: "bad name", title: "t", description: "d", source: "x" } },
      { text: "Done." },
    ]
    const run = await Scripted.run({
      home: path.join(state.root, "home-validation"),
      game,
      sidecar: state.fake,
      prompt: "Test a mod.",
      reply: (_request, index) => replies[Math.min(index, replies.length - 1)],
      env: { FAKE_SIDECAR_LOG: state.log },
    })
    try {
      expect(run.tools.map((tool) => [tool.name, tool.state.status])).toEqual([
        ["game_benchmark", "completed"],
        ["game_build_mod", "error"],
      ])
      expect((await calls(state.log)).map((call) => [call.command, call.request])).toEqual([
        ["launch-and-benchmark", { label: "test", measureSeconds: 5, gamePath: game }],
      ])
    } finally {
      await run.stop()
      rmSync(state.log, { force: true })
    }
  }, 180_000)

  test.skipIf(!REAL)(
    "the real sidecar's detect result reaches the model",
    async () => {
      const real = process.env.SPECTUNER_TEST_GAME
      const game = real ? path.resolve(real) : fakeGame()
      await detectRun({ home: path.join(state.root, "home-real"), game, sidecar: REAL ?? "" }, async (envelope) => {
        // An empty folder is refused as GAME_NOT_FOUND; a real supported game is detected.
        if (!real) return expect(envelope).toMatchObject({ ok: false, error: { code: "GAME_NOT_FOUND" } })
        expect(envelope).toMatchObject({ ok: true, data: { supported: true } })
      })
    },
    180_000,
  )
})
