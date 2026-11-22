import { expect, test } from "bun:test"
import { createRoot } from "solid-js"
import { migrateKeybinds } from "@/settings/keybinds/migration"

// Every keybind id a built-in stored under an older name, and the id it reaches. Each row fails without its mapping.
test.each([
  {
    name: "keybind summary.toggle, the details plugin's earlier id → details.toggle",
    home: () => renamed([["summary.toggle", "f7"]]),
    expected: { "details.toggle": "f7" },
  },
  {
    name: "keybind session.summary.toggle, from before plugins → details.toggle",
    home: () => renamed([["session.summary.toggle", "f8"]]),
    expected: { "details.toggle": "f8" },
  },
])("$name", (row) => {
  expect(row.home()).toEqual(row.expected)
})

/** Runs the keybind migration over these stored overrides and returns the overrides it leaves. */
function renamed(stored: readonly (readonly [string, string])[]) {
  const keybinds = new Map(stored)

  const dispose = createRoot((dispose) => {
    migrateKeybinds({
      ready: () => true,
      keybinds: {
        get: (id: string) => keybinds.get(id),
        set: (id: string, value: string) => keybinds.set(id, value),
        reset: (id: string) => keybinds.delete(id),
      },
    })

    return dispose
  })

  dispose()

  return Object.fromEntries(keybinds)
}
