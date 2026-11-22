import { expect, test } from "bun:test"
import path from "node:path"
import { devToolsEnabled } from "./devtools"

test("DevTools open only in the unpackaged development run", () => {
  expect(devToolsEnabled(false)).toBe(true)
  expect(devToolsEnabled(true)).toBe(false)
})

// Every app window is created in one of these two places; each must take its DevTools setting from the policy.
test.each(["early.ts", "appearance.ts"])("%s creates windows with the DevTools policy", async (file) => {
  const source = await Bun.file(path.join(import.meta.dirname, file)).text()
  expect(source).toContain("devTools: devToolsEnabled(app.isPackaged)")
})

test("the menu action does not open DevTools in a packaged build", async () => {
  const source = await Bun.file(path.join(import.meta.dirname, "../native/menu-actions.ts")).text()
  const action = source.slice(source.indexOf('case "view.toggleDevTools":'))
  expect(action.slice(0, action.indexOf("return"))).toContain("devToolsEnabled(app.isPackaged)")
})
