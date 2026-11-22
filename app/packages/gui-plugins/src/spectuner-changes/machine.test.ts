import { expect, test } from "bun:test"
import type { SpecTunerRpc } from "@spectuner/modder/rpc"
import {
  createChangesState,
  RETRY_DELAY_MS,
  selectChangesView,
  transitionChanges,
  type ChangesEvent,
  type ChangesState,
} from "./machine"

const change = (id: string, kind: string, mod: SpecTunerRpc.ModBuild | null = null): SpecTunerRpc.Change => ({
  id,
  kind,
  title: `Change ${id}`,
  description: "One sentence.",
  createdAt: "2026-11-21T03:00:00Z",
  status: "active",
  mod,
})

const status: SpecTunerRpc.Status = {
  game: "Sample Game",
  supported: true,
  message: null,
  changes: [change("c0003", "quality"), change("c0002", "probe"), change("c0001", "loader")],
  canRestore: true,
  past: [],
  pastTotal: 0,
  pastTruncated: false,
}

const past = (id: string, extra: Partial<SpecTunerRpc.PastChange> = {}): SpecTunerRpc.PastChange => ({
  id,
  kind: "quality",
  title: `Change ${id}`,
  description: "One sentence.",
  createdAt: "2026-11-21T02:00:00Z",
  endedAt: "2026-11-21T02:30:00Z",
  reason: "undone",
  canReapply: true,
  whyNot: null,
  mod: null,
  reappliedFrom: null,
  ...extra,
})

const history: SpecTunerRpc.PastChange[] = [
  past("c0009", { kind: "mod", reason: "replaced", mod: { name: "DoubleJump", build: 2 } }),
  past("c0008", {
    kind: "mod",
    reason: "failed_test",
    canReapply: false,
    whyNot: "It did not load when the game started.",
    mod: { name: "DoubleJump", build: 1 },
  }),
  past("c0007", { kind: "mod", reason: "restored", mod: { name: "Fly", build: 1 } }),
  past("c0006"),
]

/** What "Restore game" leaves: no changes at all, and everything earlier in the past list. */
const restored: SpecTunerRpc.Status = {
  ...status,
  changes: [],
  canRestore: false,
  past: history,
  pastTotal: 4,
  pastTruncated: false,
}

const replay = (...events: ChangesEvent[]) =>
  events.reduce(
    (current, event) => {
      const next = transitionChanges(current.state, event)

      return { state: next.state, effects: [...current.effects, ...next.effects] }
    },
    { state: createChangesState().state, effects: [...createChangesState().effects] },
  )

const ready = (): ChangesState => replay({ type: "loaded", attempt: 1, status }).state

test("a new panel reads the status and shows the player's changes, with setup as one line", () => {
  expect(createChangesState().effects).toEqual([{ type: "fetch", attempt: 1 }])
  expect(selectChangesView(createChangesState().state)).toEqual({ phase: "loading" })

  const view = selectChangesView(ready())

  expect(view).toMatchObject({
    phase: "ready",
    game: "Sample Game",
    declined: undefined,
    setup: true,
    canRestore: true,
  })
  expect(view.phase === "ready" && view.changes.map((item) => item.id)).toEqual(["c0003"])
})

test("a game SpecTuner cannot work with shows the sidecar's message", () => {
  const declined = { ...status, supported: false, message: "Online games are not supported.", changes: [] }
  const view = selectChangesView(replay({ type: "loaded", attempt: 1, status: declined }).state)

  expect(view).toMatchObject({ phase: "ready", declined: "Online games are not supported.", changes: [], setup: false })
})

test("undo runs once, shows the sidecar's sentence and reads the list again", () => {
  const started = transitionChanges(ready(), { type: "undo", changeId: "c0003" })

  expect(started.effects).toEqual([{ type: "undo", attempt: 2, changeId: "c0003" }])
  expect(selectChangesView(started.state)).toMatchObject({ undoing: "c0003", busy: true })
  // A second click while the first runs is ignored.
  expect(transitionChanges(started.state, { type: "restore" }).state).toBe(started.state)

  const done = transitionChanges(started.state, {
    type: "action.done",
    attempt: 2,
    result: { ok: true, message: "Undid the change." },
  })

  expect(done.effects).toEqual([{ type: "fetch", attempt: 3 }])
  expect(selectChangesView(done.state)).toMatchObject({
    busy: false,
    undoing: undefined,
    notice: { ok: true, message: "Undid the change." },
  })
  // The list stays on screen while it is read again.
  expect(selectChangesView(done.state).phase).toBe("ready")
})

test("a refused undo shows the sidecar's message as is", () => {
  const started = transitionChanges(ready(), { type: "undo", changeId: "c0001" }).state

  const refused = transitionChanges(started, {
    type: "action.done",
    attempt: 2,
    result: { ok: false, message: "The game is running. Close it first." },
  }).state

  expect(selectChangesView(refused)).toMatchObject({
    notice: { ok: false, message: "The game is running. Close it first." },
  })
  expect(selectChangesView(transitionChanges(refused, { type: "notice.dismiss" }).state)).toMatchObject({
    notice: undefined,
  })
})

test("restore that cannot reach the server ends in a plain error, not a stuck button", () => {
  const started = transitionChanges(ready(), { type: "restore" })

  expect(started.effects).toEqual([{ type: "restore", attempt: 2 }])
  expect(selectChangesView(started.state)).toMatchObject({ restoring: true })

  const failed = transitionChanges(started.state, { type: "action.failed", attempt: 2, message: "server unavailable" })

  expect(selectChangesView(failed.state)).toMatchObject({
    restoring: false,
    busy: false,
    notice: { ok: false, message: "server unavailable" },
  })
})

test("late answers are ignored: an older read, and an action result for another attempt", () => {
  const refreshing = transitionChanges(ready(), { type: "refresh" }).state
  const stale = { ...status, game: "Old" }

  expect(transitionChanges(refreshing, { type: "loaded", attempt: 1, status: stale }).state).toBe(refreshing)
  expect(
    transitionChanges(refreshing, { type: "action.done", attempt: 9, result: { ok: true, message: "x" } }).state,
  ).toBe(refreshing)
})

test("a failing first read stays quiet while the plugin loads, is retried, and shows its message after three", () => {
  const first = transitionChanges(createChangesState().state, { type: "load.failed", attempt: 1, message: "not found" })

  expect(first.effects).toEqual([{ type: "retry", attempt: 1, delay: RETRY_DELAY_MS }])
  expect(selectChangesView(first.state)).toEqual({ phase: "loading" })

  const retried = transitionChanges(first.state, { type: "retry.due", attempt: 1 })

  expect(retried.effects).toEqual([{ type: "fetch", attempt: 2 }])

  const failedTwice = transitionChanges(retried.state, { type: "load.failed", attempt: 2, message: "not found" }).state

  const third = transitionChanges(transitionChanges(failedTwice, { type: "retry.due", attempt: 2 }).state, {
    type: "load.failed",
    attempt: 3,
    message: "server unavailable",
  })

  expect(selectChangesView(third.state)).toEqual({ phase: "failed", message: "server unavailable" })
  expect(third.effects).toEqual([{ type: "retry", attempt: 3, delay: RETRY_DELAY_MS }])
  // A retry for an older read does nothing; an answer after the retries shows the game.
  expect(transitionChanges(third.state, { type: "retry.due", attempt: 1 }).state).toBe(third.state)
  expect(
    selectChangesView(
      transitionChanges(transitionChanges(third.state, { type: "retry.due", attempt: 3 }).state, {
        type: "loaded",
        attempt: 4,
        status,
      }).state,
    ).phase,
  ).toBe("ready")
})

test("a failed refresh keeps the list and is not retried", () => {
  const refreshing = transitionChanges(ready(), { type: "refresh" }).state
  const failed = transitionChanges(refreshing, { type: "load.failed", attempt: 2, message: "x" })

  expect(selectChangesView(failed.state).phase).toBe("ready")
  expect(failed.effects).toEqual([])
})

test("past changes are listed after Restore game, open by default, with a version only for a mod built twice", () => {
  const view = selectChangesView(replay({ type: "loaded", attempt: 1, status: restored }).state)

  expect(view).toMatchObject({ phase: "ready", changes: [], setup: false, pastOpen: true, pastTotal: 4 })
  expect(view.phase === "ready" && view.past.map((item) => [item.id, item.version])).toEqual([
    ["c0009", 2],
    ["c0008", 1],
    ["c0007", undefined],
    ["c0006", undefined],
  ])
})

test("past changes start collapsed when something is changed now, and the header toggles them", () => {
  const withPast = { ...status, past: history, pastTotal: 60, pastTruncated: true }
  const loaded = replay({ type: "loaded", attempt: 1, status: withPast }).state

  expect(selectChangesView(loaded)).toMatchObject({ pastOpen: false, pastTotal: 60, pastTruncated: true })

  const opened = transitionChanges(loaded, { type: "past.toggle" })

  expect(opened.effects).toEqual([])
  expect(selectChangesView(opened.state)).toMatchObject({ pastOpen: true })
  expect(selectChangesView(transitionChanges(opened.state, { type: "past.toggle" }).state)).toMatchObject({
    pastOpen: false,
  })
})

test("a later read does not open or close the past list on its own", () => {
  const loaded = replay({ type: "loaded", attempt: 1, status: restored }).state
  const refreshing = transitionChanges(loaded, { type: "refresh" }).state
  // After "Apply again" something is changed again; the list the player is using stays open.
  const again = transitionChanges(refreshing, {
    type: "loaded",
    attempt: 2,
    status: { ...restored, changes: [change("c0010", "quality")] },
  })

  expect(selectChangesView(again.state)).toMatchObject({ pastOpen: true })
})

test("apply again runs once, disables every other action, then shows the sentence and reads the list again", () => {
  const loaded = replay({ type: "loaded", attempt: 1, status: restored }).state
  const started = transitionChanges(loaded, { type: "reapply", changeId: "c0006" })

  expect(started.effects).toEqual([{ type: "reapply", attempt: 2, changeId: "c0006" }])
  expect(selectChangesView(started.state)).toMatchObject({
    reapplying: "c0006",
    undoing: undefined,
    restoring: false,
    busy: true,
  })
  // Undo, restore and another "Apply again" are ignored while it runs.
  expect(transitionChanges(started.state, { type: "undo", changeId: "c0003" }).state).toBe(started.state)
  expect(transitionChanges(started.state, { type: "restore" }).state).toBe(started.state)
  expect(transitionChanges(started.state, { type: "reapply", changeId: "c0007" }).state).toBe(started.state)

  const done = transitionChanges(started.state, {
    type: "action.done",
    attempt: 2,
    result: { ok: true, message: 'Put back "Change c0006". Saved 1 setting.' },
  })

  expect(done.effects).toEqual([{ type: "fetch", attempt: 3 }])
  expect(selectChangesView(done.state)).toMatchObject({
    busy: false,
    reapplying: undefined,
    notice: { ok: true, message: 'Put back "Change c0006". Saved 1 setting.' },
  })
})

test("a failed apply again shows the plain error and frees the panel", () => {
  const loaded = replay({ type: "loaded", attempt: 1, status: restored }).state
  const started = transitionChanges(loaded, { type: "reapply", changeId: "c0007" }).state

  const refused = transitionChanges(started, {
    type: "action.done",
    attempt: 2,
    result: { ok: false, message: "The mod did not load when the game started, so it was removed again." },
  })

  expect(refused.effects).toEqual([{ type: "fetch", attempt: 3 }])
  expect(selectChangesView(refused.state)).toMatchObject({
    busy: false,
    reapplying: undefined,
    notice: { ok: false, message: "The mod did not load when the game started, so it was removed again." },
  })

  const lost = transitionChanges(transitionChanges(refused.state, { type: "reapply", changeId: "c0006" }).state, {
    type: "action.failed",
    attempt: 4,
    message: "server unavailable",
  })

  expect(selectChangesView(lost.state)).toMatchObject({
    busy: false,
    notice: { ok: false, message: "server unavailable" },
  })
})

test("a mod in place and the same mod under Past changes both say which version they are", () => {
  // Build 1 of Fly is in place, build 2 was undone; DoubleJump's builds 1 and 2 are both past; Glide has one build.
  const mixed: SpecTunerRpc.Status = {
    ...status,
    changes: [
      change("c0011", "mod", { name: "Fly", build: 1 }),
      change("c0010", "mod", { name: "Glide", build: 1 }),
      change("c0003", "quality"),
      change("c0001", "loader"),
    ],
    past: [
      past("c0012", { kind: "mod", mod: { name: "Fly", build: 2 } }),
      ...history.filter((item) => item.mod?.name === "DoubleJump"),
      past("c0006"),
    ],
    pastTotal: 4,
  }
  const view = selectChangesView(replay({ type: "loaded", attempt: 1, status: mixed }).state)

  expect(view.phase === "ready" && view.changes.map((item) => [item.id, item.version])).toEqual([
    ["c0011", 1],
    ["c0010", undefined],
    ["c0003", undefined],
  ])
  expect(view.phase === "ready" && view.past.map((item) => [item.id, item.version])).toEqual([
    ["c0012", 2],
    ["c0009", 2],
    ["c0008", 1],
    ["c0006", undefined],
  ])
})

test("the same build listed twice is still one version, so it gets no label", () => {
  const same: SpecTunerRpc.Status = {
    ...status,
    changes: [change("c0011", "mod", { name: "Fly", build: 1 })],
    past: [past("c0007", { kind: "mod", mod: { name: "Fly", build: 1 } })],
    pastTotal: 1,
  }
  const view = selectChangesView(replay({ type: "loaded", attempt: 1, status: same }).state)

  expect(view).toMatchObject({
    changes: [{ id: "c0011", version: undefined }],
    past: [{ id: "c0007", version: undefined }],
  })
})

test("Play starts the game with the changes, disables every other action, and shows the sentence", () => {
  const started = transitionChanges(ready(), { type: "play", withChanges: true })

  expect(started.effects).toEqual([{ type: "play", attempt: 2, withChanges: true }])
  expect(selectChangesView(started.state)).toMatchObject({ playing: "with", busy: true })
  // Undo, restore, Apply again and a second start are ignored while it runs.
  expect(transitionChanges(started.state, { type: "undo", changeId: "c0003" }).state).toBe(started.state)
  expect(transitionChanges(started.state, { type: "restore" }).state).toBe(started.state)
  expect(transitionChanges(started.state, { type: "reapply", changeId: "c0006" }).state).toBe(started.state)
  expect(transitionChanges(started.state, { type: "play", withChanges: false }).state).toBe(started.state)

  const done = transitionChanges(started.state, {
    type: "action.done",
    attempt: 2,
    result: { ok: true, message: "Started Sample Game with your changes.", steamLaunchOption: null, note: null },
  })

  expect(done.effects).toEqual([{ type: "fetch", attempt: 3 }])
  const view = selectChangesView(done.state)
  expect(view).toMatchObject({ playing: undefined, busy: false })
  // No note and no launch option: the notice is the sentence alone.
  expect(view.phase === "ready" && view.notice).toEqual({ ok: true, message: "Started Sample Game with your changes." })
})

test("Play without changes is its own start, and an undo cannot run beside it", () => {
  const started = transitionChanges(ready(), { type: "play", withChanges: false })

  expect(started.effects).toEqual([{ type: "play", attempt: 2, withChanges: false }])
  expect(selectChangesView(started.state)).toMatchObject({ playing: "without", busy: true, undoing: undefined })
})

test("a start on Linux or macOS from a Steam game shows the note and the launch option to paste", () => {
  const started = transitionChanges(ready(), { type: "play", withChanges: true }).state
  const done = transitionChanges(started, {
    type: "action.done",
    attempt: 2,
    result: {
      ok: true,
      message: "Started Sample Game with your changes.",
      steamLaunchOption: '"/games/run_bepinex.sh" %command%',
      note: "Steam may start the game without your changes.",
    },
  })

  expect(selectChangesView(done.state)).toMatchObject({
    notice: {
      ok: true,
      message: "Started Sample Game with your changes.",
      note: "Steam may start the game without your changes.",
      steamLaunchOption: '"/games/run_bepinex.sh" %command%',
    },
  })
})

test("a refused start shows the plain message and frees the buttons", () => {
  const started = transitionChanges(ready(), { type: "play", withChanges: true }).state
  const refused = transitionChanges(started, {
    type: "action.done",
    attempt: 2,
    result: { ok: false, message: "The game is already running.", steamLaunchOption: null, note: null },
  })

  expect(selectChangesView(refused.state)).toMatchObject({
    busy: false,
    playing: undefined,
    notice: { ok: false, message: "The game is already running." },
  })

  const lost = transitionChanges(transitionChanges(refused.state, { type: "play", withChanges: true }).state, {
    type: "action.failed",
    attempt: 4,
    message: "server unavailable",
  })

  expect(selectChangesView(lost.state)).toMatchObject({
    busy: false,
    notice: { ok: false, message: "server unavailable" },
  })
})

test("Play is offered when SpecTuner can work with the game; without changes only when something is in place", () => {
  expect(selectChangesView(ready())).toMatchObject({ canPlay: true, canPlayWithout: true })

  const untouched = { ...status, changes: [], canRestore: false }
  expect(selectChangesView(replay({ type: "loaded", attempt: 1, status: untouched }).state)).toMatchObject({
    canPlay: true,
    canPlayWithout: false,
  })

  // SpecTuner's setup alone is something to compare against.
  const setupOnly = { ...status, changes: [change("c0002", "probe"), change("c0001", "loader")] }
  expect(selectChangesView(replay({ type: "loaded", attempt: 1, status: setupOnly }).state)).toMatchObject({
    canPlay: true,
    canPlayWithout: true,
  })

  const declined = { ...status, supported: false, message: "Online games are not supported." }
  expect(selectChangesView(replay({ type: "loaded", attempt: 1, status: declined }).state)).toMatchObject({
    canPlay: false,
    canPlayWithout: false,
  })
})
