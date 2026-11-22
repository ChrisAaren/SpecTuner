import { expect, test } from "bun:test"
import {
  ASSUMED_CONTEXT,
  createLocalModelState,
  isSmallContext,
  SMALL_CONTEXT,
  selectLocalModelView,
  transitionLocalModel,
  type LocalModelEffect,
  type LocalModelEvent,
  type LocalModelState,
} from "./local-model-machine"

const replay = (state: LocalModelState, ...events: LocalModelEvent[]) =>
  events.reduce(
    (current, event) => {
      const next = transitionLocalModel(current.state, event)

      return { state: next.state, effects: [...current.effects, ...next.effects] }
    },
    { state, effects: new Array<LocalModelEffect>() },
  )

const typed = replay(
  createLocalModelState(),
  { type: "address.changed", value: "http://127.0.0.1:8080/v1" },
  { type: "key.changed", value: "  " },
).state

const found = {
  ok: true,
  reason: null,
  address: "http://127.0.0.1:8080/v1",
  models: [
    { id: "small", context: 4096 },
    { id: "large", context: null },
  ],
}

test("find, pick, save: the chosen model is saved with the window the server reported, then used", () => {
  const run = replay(
    typed,
    { type: "find" },
    { type: "found", attempt: 1, result: found },
    { type: "model.selected", model: "large" },
    { type: "save" },
    { type: "saved", attempt: 2, result: { ok: true, reason: null } },
  )

  expect(run.effects).toEqual([
    { type: "find", attempt: 1, address: "http://127.0.0.1:8080/v1", key: undefined },
    { type: "save", attempt: 2, address: "http://127.0.0.1:8080/v1", key: undefined, model: "large", context: null },
    { type: "use", model: "large" },
  ])
  expect(selectLocalModelView(run.state).saved).toBe("large")
})

test("nothing to find without an address, and one request at a time", () => {
  expect(transitionLocalModel(createLocalModelState(), { type: "find" }).effects).toEqual([])

  const finding = transitionLocalModel(typed, { type: "find" }).state

  expect(selectLocalModelView(finding)).toMatchObject({ finding: true, canFind: false })
  expect(transitionLocalModel(finding, { type: "find" }).state).toBe(finding)
  expect(transitionLocalModel(finding, { type: "address.changed", value: "x" }).state).toBe(finding)
})

test("a server that answers nothing usable shows why, and the address can be fixed", () => {
  const failed = replay(
    typed,
    { type: "find" },
    {
      type: "found",
      attempt: 1,
      result: { ok: false, reason: "unreachable", address: "http://127.0.0.1:8080/v1", models: [] },
    },
  ).state

  expect(selectLocalModelView(failed)).toMatchObject({ failure: "unreachable", canFind: true, models: [] })

  const edited = transitionLocalModel(failed, { type: "address.changed", value: "http://127.0.0.1:8081/v1" }).state

  expect(selectLocalModelView(edited).failure).toBeUndefined()
})

test("a late answer for an older attempt is ignored; a lost request ends in an error, not a spinner", () => {
  const finding = transitionLocalModel(typed, { type: "find" }).state

  expect(transitionLocalModel(finding, { type: "found", attempt: 7, result: found }).state).toBe(finding)
  expect(
    selectLocalModelView(transitionLocalModel(finding, { type: "request.failed", attempt: 1 }).state),
  ).toMatchObject({ finding: false, failure: "request" })

  const saving = replay(finding, { type: "found", attempt: 1, result: found }, { type: "save" }).state

  const refused = transitionLocalModel(saving, {
    type: "saved",
    attempt: 2,
    result: { ok: false, reason: "unreadable" },
  })

  expect(selectLocalModelView(refused.state)).toMatchObject({ failure: "unreadable", canSave: true, selected: "small" })
  expect(refused.effects).toEqual([])
})

test("a window under 16,000 tokens, or none reported (the assumed 8192), warns for the chosen model", () => {
  expect(SMALL_CONTEXT).toBe(16_000)
  expect(isSmallContext(15_999)).toBe(true)
  expect(isSmallContext(16_000)).toBe(false)
  expect(isSmallContext(32_768)).toBe(false)
  expect(isSmallContext(null)).toBe(ASSUMED_CONTEXT < SMALL_CONTEXT)

  const listed = replay(
    typed,
    { type: "find" },
    {
      type: "found",
      attempt: 1,
      result: { ...found, models: [{ id: "roomy", context: 32_768 }, ...found.models] },
    },
  ).state

  expect(selectLocalModelView(listed)).toMatchObject({ selected: "roomy", small: false })
  expect(selectLocalModelView(transitionLocalModel(listed, { type: "model.selected", model: "small" }).state).small).toBe(
    true,
  )
  expect(selectLocalModelView(transitionLocalModel(listed, { type: "model.selected", model: "large" }).state).small).toBe(
    true,
  )
  expect(selectLocalModelView(typed).small).toBe(false)
})
