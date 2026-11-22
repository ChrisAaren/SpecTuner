import { afterEach, beforeAll, describe, expect, test } from "bun:test"
import { mkdtempSync, rmSync } from "node:fs"
import os from "node:os"
import path from "node:path"
import { SpecTunerChanges } from "../plugin/changes.ts"
import { SpecTunerTools } from "../plugin/tools.ts"
import { calls, fakeSidecar } from "./fixture/sidecar.ts"

const GAME = "D:\\Games\\My Game"

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

/** The fake sidecar in `mode`, logging its calls to a fresh file. */
function useFake(mode?: string) {
  state.dir = mkdtempSync(path.join(os.tmpdir(), "spectuner-changes-test-"))
  const log = path.join(state.dir, "calls.jsonl")
  process.env.FAKE_SIDECAR_LOG = log
  if (mode) process.env.FAKE_SIDECAR_MODE = mode
  if (!mode) delete process.env.FAKE_SIDECAR_MODE

  return { log, options: { game: GAME, exe: () => state.exe } }
}

describe("SpecTunerChanges.status", () => {
  test("a mod build that a newer build replaced is listed as past, not as in place", async () => {
    const status = await SpecTunerChanges.status(useFake("ledger-replaced").options)

    expect(status.changes.map((change) => [change.id, change.mod?.build])).toEqual([["c0008", 2]])
    expect(status.past.map((change) => [change.id, change.reason, change.mod?.build])).toEqual([["c0005", "replaced", 1]])
  })

  test("names the game and lists only the changes still in place, newest first", async () => {
    const fake = useFake("ledger")
    const status = await SpecTunerChanges.status(fake.options)

    expect(status).toMatchObject({ game: "Ledger Game", supported: true, message: null, canRestore: true })
    expect(status.changes.map((change) => [change.id, change.kind, change.status])).toEqual([
      ["c0007", "mod", "active"],
      ["c0003", "quality", "active"],
      ["c0002", "probe", "active"],
      ["c0001", "loader", "pending"],
    ])
    expect(status.changes[1]).toEqual({
      id: "c0003",
      kind: "quality",
      title: "Turn off anti-aliasing",
      description: "Turn off anti-aliasing.",
      createdAt: "2026-11-21T03:00:00Z",
      status: "active",
      mod: null,
    })
    expect((await calls(fake.log)).map((call) => [call.command, call.request])).toEqual([
      ["detect", { gamePath: GAME }],
      ["changes", { gamePath: GAME }],
    ])
  })

  test("a current mod change carries its mod name and build from the sidecar's mods map", async () => {
    const status = await SpecTunerChanges.status(useFake("ledger").options)

    // c0003's value in the map is unreadable, so it reads as no mod; the others are not in the map at all.
    expect(status.changes.map((change) => [change.id, change.mod])).toEqual([
      ["c0007", { name: "DoubleJump", build: 3 }],
      ["c0003", null],
      ["c0002", null],
      ["c0001", null],
    ])
  })

  test("a sidecar without the mods map still lists every change, without builds", async () => {
    const status = await SpecTunerChanges.status(useFake("ledger-no-mods").options)

    expect(status.changes.map((change) => [change.id, change.mod])).toEqual([
      ["c0007", null],
      ["c0003", null],
      ["c0002", null],
      ["c0001", null],
    ])
    expect(status.past.map((item) => item.id)).toEqual(["c0006", "c0005", "c0004"])
  })

  test("lists past changes newest first, with whether each can be put back and why not", async () => {
    const status = await SpecTunerChanges.status(useFake("ledger").options)

    // The entry without an id is left out; a reason this app does not know reads as undone.
    expect(status.past).toEqual([
      {
        id: "c0006",
        kind: "mod",
        title: "Double the jump height",
        description: "Jumps go twice as high.",
        createdAt: "2026-11-21T06:00:00Z",
        endedAt: "2026-11-21T06:30:00Z",
        reason: "restored",
        canReapply: true,
        whyNot: null,
        mod: { name: "DoubleJump", build: 2 },
        reappliedFrom: "c0001",
      },
      {
        id: "c0005",
        kind: "mod",
        title: "Double the jump height",
        description: "Jumps go twice as high.",
        createdAt: "2026-11-21T05:00:00Z",
        endedAt: "2026-11-21T05:01:00Z",
        reason: "failed_test",
        canReapply: false,
        whyNot: "This change did not load when the game started, so it cannot be put back.",
        mod: { name: "DoubleJump", build: 1 },
        reappliedFrom: null,
      },
      {
        id: "c0004",
        kind: "quality",
        title: "Lower shadow distance",
        description: "Lower shadow distance.",
        createdAt: "2026-11-21T04:00:00Z",
        endedAt: "2026-11-21T04:10:00Z",
        reason: "undone",
        canReapply: true,
        whyNot: null,
        mod: null,
        reappliedFrom: null,
      },
    ])
    expect(status).toMatchObject({ pastTotal: 60, pastTruncated: true })
  })

  test("a game SpecTuner cannot work with gives the sidecar's plain message", async () => {
    const status = await SpecTunerChanges.status(useFake("unsupported").options)

    expect(status).toEqual({
      game: "Locked Game",
      supported: false,
      message: "This game is built in a way SpecTuner cannot change yet.",
      changes: [],
      canRestore: false,
      past: [],
      pastTotal: 0,
      pastTruncated: false,
    })
  })

  test("a game with nothing changed yet has an empty list and nothing to restore", async () => {
    const status = await SpecTunerChanges.status(useFake().options)

    expect(status).toEqual({
      game: "My Game",
      supported: true,
      message: null,
      changes: [],
      canRestore: false,
      past: [],
      pastTotal: 0,
      pastTruncated: false,
    })
  })

  test("a missing helper is a plain message, not a failure", async () => {
    useFake()
    const missing = path.join(os.tmpdir(), "spectuner-does-not-exist", "spectuner-sidecar.exe")
    const status = await SpecTunerChanges.status({ game: GAME, exe: () => missing })

    expect(status.supported).toBe(false)
    expect(status.message).toMatch(/helper program was not found/)
    expect(status.changes).toEqual([])
  })
})

describe("SpecTunerChanges.undo and restore", () => {
  test("undo reverts the one change and returns the sidecar's sentence", async () => {
    const fake = useFake("ledger")

    expect(await SpecTunerChanges.undo(fake.options, "c0003")).toEqual({
      ok: true,
      message: 'Undid "Turn off anti-aliasing".',
    })
    expect((await calls(fake.log)).map((call) => [call.command, call.request])).toEqual([
      ["revert", { changeId: "c0003", gamePath: GAME }],
    ])
  })

  test("a refused undo returns the sidecar's message as is", async () => {
    expect(await SpecTunerChanges.undo(useFake("ledger").options, "c0001")).toEqual({
      ok: false,
      message: "Undo your other changes first, or restore the whole game.",
    })
  })

  test("restore reverts everything and returns the sidecar's sentence", async () => {
    const fake = useFake("ledger")

    expect(await SpecTunerChanges.restore(fake.options)).toEqual({
      ok: true,
      message: "The game is back to how SpecTuner first found it.",
    })
    expect((await calls(fake.log)).map((call) => [call.command, call.request])).toEqual([
      ["revert", { all: true, gamePath: GAME }],
    ])
  })
})

describe("SpecTunerChanges.play", () => {
  test("starts the game with the changes and returns the sidecar's sentence", async () => {
    const fake = useFake("ledger")

    expect(await SpecTunerChanges.play(fake.options, true)).toEqual({
      ok: true,
      message: "Started Ledger Game with your changes.",
      steamLaunchOption: null,
      note: null,
    })
    expect((await calls(fake.log)).map((call) => [call.command, call.request])).toEqual([
      ["play", { withChanges: true, gamePath: GAME }],
    ])
  })

  test("starts the game without the changes", async () => {
    const fake = useFake("ledger")

    expect(await SpecTunerChanges.play(fake.options, false)).toEqual({
      ok: true,
      message: "Started Ledger Game without SpecTuner's changes.",
      steamLaunchOption: null,
      note: null,
    })
    expect((await calls(fake.log)).map((call) => call.request)).toEqual([{ withChanges: false, gamePath: GAME }])
  })

  test("passes on the Steam launch option and the note when the sidecar gives them", async () => {
    expect(await SpecTunerChanges.play(useFake("play-steam").options, true)).toEqual({
      ok: true,
      message: "Started My Game with your changes.",
      steamLaunchOption: '"/games/run_bepinex.sh" %command%',
      note: "If you start the game from Steam, Steam may start it without your changes until you set the launch option.",
    })
  })

  test("a refusal returns the sidecar's plain message, with nothing to paste", async () => {
    expect(await SpecTunerChanges.play(useFake("game-running").options, true)).toEqual({
      ok: false,
      message: "The game is already running. Close it first, then try again.",
      steamLaunchOption: null,
      note: null,
    })
  })

  test("a missing helper is a plain message, not a failure", async () => {
    useFake()
    const missing = path.join(os.tmpdir(), "spectuner-does-not-exist", "spectuner-sidecar.exe")
    const result = await SpecTunerChanges.play({ game: GAME, exe: () => missing }, true)

    expect(result).toMatchObject({ ok: false, steamLaunchOption: null, note: null })
    expect(result.message).toMatch(/helper program was not found/)
  })
})

describe("SpecTunerChanges.reapply", () => {
  test("puts a past change back through the sidecar's reapply and returns its sentence", async () => {
    const fake = useFake("ledger")

    expect(await SpecTunerChanges.reapply(fake.options, "c0004")).toEqual({
      ok: true,
      message: 'Put back "Lower shadow distance". Saved 1 setting; it applies the next time the game starts.',
    })
    // Never `test`: the app always lets the sidecar check a mod.
    expect((await calls(fake.log)).map((call) => [call.command, call.request])).toEqual([
      ["reapply", { changeId: "c0004", gamePath: GAME }],
    ])
  })

  test("a change that cannot be put back returns the sidecar's plain reason", async () => {
    expect(await SpecTunerChanges.reapply(useFake("ledger").options, "c0005")).toEqual({
      ok: false,
      message: "This change did not load when the game started, so it cannot be put back.",
    })
  })
})

test("every game tool call tells the Changes panel to refresh, whatever its outcome", async () => {
  useFake()
  const finished: string[] = []
  const tools = SpecTunerTools.make(
    GAME,
    () => state.exe,
    () => finished.push("changed"),
  )
  const signal = new AbortController().signal
  const find = (name: string) => tools.find((tool) => tool.name === name)!

  await find("game_detect").execute({}, { signal } as never)
  await find("game_set_quality").execute({ title: "t", description: "d", settings: { bogus: 1 } }, { signal } as never)

  expect(finished).toEqual(["changed", "changed"])
})
