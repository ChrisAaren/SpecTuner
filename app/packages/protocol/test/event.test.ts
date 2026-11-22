import { expect, test } from "bun:test"
import { isSpecTunerEvent, SpecTunerEvent, type SpecTunerEventEncoded } from "../src/groups/event.js"

type JsonShape<Value> = Value extends string | number | boolean | null
  ? Value
  : Value extends undefined
    ? never
    : Value extends ReadonlyArray<infer Item>
      ? ReadonlyArray<JsonShape<Item>>
      : Value extends object
        ? {
            readonly [Key in keyof Value as undefined extends Value[Key] ? never : Key]: JsonShape<Value[Key]>
          } & {
            readonly [Key in keyof Value as undefined extends Value[Key] ? Key : never]?: JsonShape<
              Exclude<Value[Key], undefined>
            >
          }
        : Value

// JSON.stringify omits undefined object properties, so normalize them before
// requiring every runtime event shape to fit its encoded wire contract.
const wireReady: [JsonShape<SpecTunerEvent>] extends [JsonShape<SpecTunerEventEncoded>] ? true : false = true

test("classifies public events by type", () => {
  expect(isSpecTunerEvent({ type: "server.connected" })).toBe(true)
  expect(isSpecTunerEvent({ type: "mcp.status.changed" })).toBe(true)
  expect(isSpecTunerEvent({ type: "mcp.resources.changed" })).toBe(true)
  expect(isSpecTunerEvent({ type: "mcp.tools.changed" })).toBe(false)
  expect(isSpecTunerEvent({ type: "acme.updated" })).toBe(false)
})

test("keeps public event runtime values within the encoded contract", () => {
  expect(wireReady).toBe(true)
})
