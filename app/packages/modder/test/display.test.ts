import { afterEach, beforeAll, expect, test } from "bun:test"
import { SpecTunerDisplay } from "../plugin/display.ts"
import { SpecTunerTools } from "../plugin/tools.ts"
import { fakeSidecar } from "./fixture/sidecar.ts"

const state = { exe: "" }

const saved = { log: process.env.FAKE_SIDECAR_LOG, mode: process.env.FAKE_SIDECAR_MODE }

beforeAll(async () => {
  state.exe = await fakeSidecar()
}, 120_000)

afterEach(() => {
  process.env.FAKE_SIDECAR_LOG = saved.log
  process.env.FAKE_SIDECAR_MODE = saved.mode
})

// What the player reads for each call while it runs.
const titles: { tool: string; input: Record<string, unknown>; title: string }[] = [
  { tool: "game_detect", input: {}, title: "Checking your game" },
  { tool: "game_benchmark", input: { label: "before" }, title: "Measuring performance: before" },
  {
    tool: "game_benchmark",
    input: { label: "x", trySettings: { shadowDistance: 40 } },
    title: "Trying a setting: shadow distance",
  },
  {
    tool: "game_benchmark",
    input: { label: "x", trySettings: { shadows: "Disable", antiAliasing: 0 } },
    title: "Trying a setting: shadows off, edge smoothing off",
  },
  {
    tool: "game_set_quality",
    input: { title: "Lower shadow distance", settings: {} },
    title: "Changing a setting: Lower shadow distance",
  },
  {
    tool: "game_build_mod",
    input: { title: "Double the jump height" },
    title: "Building your change: Double the jump height",
  },
  { tool: "game_revert", input: { changeId: "c0003" }, title: "Undoing a change" },
  { tool: "game_revert", input: { all: true }, title: "Restoring the game" },
  { tool: "game_search_code", input: { query: "jump" }, title: "Looking through the game's code: jump" },
  { tool: "game_search_code", input: {}, title: "Looking through the game's code" },
  { tool: "game_read_code", input: { type: "Player" }, title: "Looking through the game's code" },
  { tool: "game_read_logs", input: {}, title: "Reading the game's log" },
  { tool: "game_changes", input: {}, title: "Listing changes" },
  { tool: "game_reapply", input: { changeId: "c0004" }, title: "Putting a change back" },
]

test.each(titles)('$tool reads as "$title"', (row) => {
  expect(SpecTunerDisplay.title(row.tool, row.input)).toBe(row.title)
})

test("every game tool has a plain title", () => {
  expect(SpecTunerTools.names.filter((name) => SpecTunerDisplay.title(name, {}) === name)).toEqual([])
})

test("a call shows its title while it runs and the sidecar's sentence when it ends", async () => {
  delete process.env.FAKE_SIDECAR_LOG
  delete process.env.FAKE_SIDECAR_MODE
  const progress: unknown[] = []
  const [detect, , quality] = SpecTunerTools.make("D:\Games\My Game", () => state.exe).map(SpecTunerDisplay.wrap)
  const context = {
    signal: new AbortController().signal,
    progress: async (update: unknown) => void progress.push(update),
  }

  const ok = await detect!.execute({}, context as never)
  const refused = await quality!.execute({ title: "Bogus", description: "d", settings: { bogus: 1 } }, context as never)

  expect(progress).toEqual([
    { display: { title: "Checking your game" } },
    { display: { title: "Changing a setting: Bogus" } },
  ])
  expect(ok.metadata?.display).toEqual({
    title: "Checking your game",
    subtitle: "This is a Unity game SpecTuner can work with.",
  })
  expect(refused.metadata).toEqual({
    title: "One of those settings does not exist.",
    ok: false,
    code: "UNKNOWN_SETTING",
    display: { title: "Changing a setting: Bogus", subtitle: "One of those settings does not exist." },
  })
  // The model still reads the whole envelope.
  expect(JSON.parse(String(refused.content)).error.code).toBe("UNKNOWN_SETTING")
})
