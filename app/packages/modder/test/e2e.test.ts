import { afterAll, beforeAll, describe, expect, test } from "bun:test"
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { SpecTunerE2E } from "../script/e2e.ts"
import { Scripted } from "../script/scripted.ts"
import { SpecTunerServer } from "../script/server.ts"
import { SpecTunerConfig } from "../src/config.ts"
import { calls, fakeSidecar } from "./fixture/sidecar.ts"

const GAME_TOOLS: string[] = [...SpecTunerConfig.TOOLS].sort()
const state = { root: "", sidecar: "", model: undefined as Scripted.Model | undefined }

beforeAll(async () => {
  state.root = mkdtempSync(path.join(os.tmpdir(), "spectuner-e2e-"))
  state.sidecar = await fakeSidecar()
  state.model = Scripted.model(Scripted.perPrompt)
}, 120_000)

afterAll(async () => {
  await state.model?.stop()
  if (state.root) rmSync(state.root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 })
})

function options(name: string, extra: Partial<SpecTunerE2E.Options>): SpecTunerE2E.Options {
  const game = path.join(state.root, name, "game")
  mkdirSync(game, { recursive: true })
  const lines: string[] = []
  return {
    game,
    prompts: ["Is my game supported?"],
    model: `${Scripted.PROVIDER}/${Scripted.MODEL}`,
    providerConfig: { providers: Scripted.providers(state.model?.url ?? "") },
    sidecar: state.sidecar,
    pluginDir: SpecTunerServer.PLUGIN_DIR,
    home: path.join(state.root, name, "home"),
    out: path.join(state.root, name, "out"),
    turnTimeoutMs: 60_000,
    recordTools: true,
    print: (line) => lines.push(line),
    ...extra,
  }
}

describe("e2e driver", () => {
  test("two prompts in one session: progress, transcript and the recorded tool list", async () => {
    const lines: string[] = []
    const run = options("two", {
      prompts: ["Is my game supported?", "What changes have you made?"],
      print: (line) => lines.push(line),
    })
    const transcript = await SpecTunerE2E.run(run)
    expect(transcript.ok).toBe(true)
    expect(transcript.agents).toEqual(["modder"])
    expect(transcript.selectableAgents).toEqual(["modder"])
    expect(transcript.turns.map((turn) => turn.outcome)).toEqual(["succeeded", "succeeded"])
    expect(transcript.toolCalls).toEqual({ game_detect: 1, game_changes: 1 })
    const first = transcript.turns[0].steps[0]
    expect(first.kind === "tool" && JSON.parse(first.result ?? "")).toMatchObject({
      ok: true,
      data: { supported: true },
    })
    expect(transcript.turns[1].answer).toBe("I looked at your game and everything is in order.")
    // Every agent request offered exactly the game tools; title requests offer none.
    const agentRequests = (transcript.modelRequests ?? []).filter((request) => request.tools !== null)
    expect(agentRequests.length).toBe(4)
    agentRequests.forEach((request) => expect([...(request.tools ?? [])].sort()).toEqual(GAME_TOOLS))
    expect(transcript.toolsOffered).toEqual(GAME_TOOLS)
    // Progress lines as the turn ran, then the summary.
    expect(lines).toContain("  - game_detect ")
    expect(lines.some((line) => /^ {4}game_changes completed [\d.]+s: No changes yet\.$/.test(line))).toBe(true)
    expect(lines).toContain("tool calls: game_detect x1, game_changes x1")
    expect(existsSync(path.join(run.out, "transcript.json"))).toBe(true)
    expect(readFileSync(path.join(run.out, "transcript.md"), "utf8")).toContain(
      "## Prompt 2: What changes have you made?",
    )
  }, 180_000)

  test("a trial reaches the sidecar with trySettings but without the knobs the model added", async () => {
    const log = path.join(state.root, "trial-calls.jsonl")
    const transcript = await SpecTunerE2E.run(
      options("trial", { prompts: ["Try shorter shadows"], env: { FAKE_SIDECAR_LOG: log } }),
    )
    expect(transcript.ok).toBe(true)
    const step = transcript.turns[0].steps[0]
    // The model sent `pairs: 2`; the sidecar got only the tool's real inputs.
    expect(step.kind === "tool" && step.input).toMatchObject({ pairs: 2 })
    expect((await calls(log)).map((call) => [call.command, call.request])).toEqual([
      [
        "launch-and-benchmark",
        { label: "try: shorter shadows", trySettings: { shadowDistance: 20 }, gamePath: options("trial", {}).game },
      ],
    ])
    expect(step.kind === "tool" && JSON.parse(step.result ?? "")).toMatchObject({
      ok: true,
      data: { trial: { verdict: "better", settings: { shadowDistance: 20 } } },
    })
  }, 180_000)

  test("a player config cannot undo the lock-down", async () => {
    const transcript = await SpecTunerE2E.run(
      options("hostile", {
        providerConfig: {
          providers: Scripted.providers(state.model?.url ?? ""),
          default_agent: "build",
          permissions: [{ action: "*", resource: "*", effect: "allow" }],
          agents: {
            build: { disabled: false, mode: "primary" },
            modder: { permissions: [{ action: "*", resource: "*", effect: "allow" }] },
          },
          plugins: ["spectuner.tool.shell", "spectuner.tool.read", "spectuner.browser"],
          experimental: { full_codemode: true, todo: true },
        },
      }),
    )
    expect(transcript.ok).toBe(true)
    expect(transcript.selectableAgents).toEqual(["modder"])
    expect(transcript.agents).toEqual(["modder"])
    expect(transcript.toolsOffered).toEqual(GAME_TOOLS)
  }, 180_000)

  const helper = { mode: "primary", permissions: [{ action: "*", resource: "*", effect: "allow" }] }

  test("an agent added by a config file is removed; the modder stays the only one", async () => {
    const transcript = await SpecTunerE2E.run(
      options("extra-agent", {
        providerConfig: { providers: Scripted.providers(state.model?.url ?? ""), agents: { helper } },
      }),
    )
    expect(transcript.ok).toBe(true)
    expect(transcript.selectableAgents).toEqual(["modder"])
    expect(transcript.agents).toEqual(["modder"])
    expect(transcript.toolsOffered).toEqual(GAME_TOOLS)
  }, 180_000)

  test("a session that asks for the added agent gets nothing beyond the game tools", async () => {
    const model = Scripted.model(Scripted.perPrompt)
    const home = path.join(state.root, "ask-helper", "home")
    const game = path.join(state.root, "ask-helper", "game")
    mkdirSync(game, { recursive: true })
    const configDir = path.join(home, "config", "spectuner")
    mkdirSync(configDir, { recursive: true })
    writeFileSync(
      path.join(configDir, "spectuner.json"),
      JSON.stringify({ providers: Scripted.providers(model.url), agents: { helper } }),
    )
    const server = await SpecTunerServer.start({ home, sidecar: state.sidecar })
    try {
      const query = SpecTunerServer.location(game)
      await SpecTunerServer.until(async () => {
        const plugins = (await (await server.fetch(`/api/plugin?${query}`)).json()).data
        const models = (await (await server.fetch(`/api/model?${query}`)).json()).data
        return (
          plugins.some(
            (plugin: { id: string; state: { status: string } }) =>
              plugin.id === "spectuner.modder" && plugin.state.status === "active",
          ) && models.some((item: { id: string }) => item.id === Scripted.MODEL)
        )
      }, 60_000)
      const agents = async (): Promise<string[]> =>
        (await (await server.fetch(`/api/agent?${query}`)).json()).data.map((agent: { id: string }) => agent.id).sort()
      // The config's agent can show for a moment while plugins load; the lock then removes it and it stays removed.
      await SpecTunerServer.until(async () => !(await agents()).includes("helper"), 15_000)
      await Bun.sleep(1000)
      expect(await agents()).toEqual(["compaction", "modder", "summary", "title"])

      const created = await server.fetch("/api/session", {
        body: {
          location: { directory: game },
          agent: "helper",
          model: { providerID: Scripted.PROVIDER, id: Scripted.MODEL },
        },
      })
      if (created.ok) {
        const id = (await created.json()).data.id
        await server.fetch(`/api/session/${id}/prompt`, { body: { text: "Is my game supported?" } })
        await server.fetch(`/api/experimental/session/${id}/wait`, { method: "POST" })
      }
      // Whatever the server did with the request (refuse it, or run it some other way), no model request offered
      // anything beyond the game tools.
      const offered = model.requests
        .filter((request) => !Scripted.isTitle(request))
        .flatMap((request) => (request.tools ?? []).map((tool) => tool.function.name))
      expect(offered.filter((name) => !GAME_TOOLS.includes(name))).toEqual([])
      console.log(
        `session with agent "helper": ${created.status}; agent requests: ${model.requests.filter((request) => !Scripted.isTitle(request)).length}`,
      )
    } finally {
      await server.stop()
      await model.stop()
    }
  }, 180_000)

  test("secrets and the user-profile folder never reach output", () => {
    const clean = SpecTunerE2E.cleaner(["sk-secret-123"])
    const home = os.homedir()
    expect(clean(`key sk-secret-123 at ${home}\\x and ${home.replaceAll("\\", "/")}/y`)).toBe(
      "key [redacted] at %USERPROFILE%\\x and %USERPROFILE%/y",
    )
    // In JSON text the backslashes are doubled; that spelling is replaced too.
    expect(clean(JSON.stringify({ path: `${home}\\z` }))).toBe(String.raw`{"path":"%USERPROFILE%\\z"}`)
  })
})
