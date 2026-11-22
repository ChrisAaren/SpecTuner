import { expect, test } from "bun:test"
import { configOptions } from "../src/config-options"

// Machine-wide config and macOS managed preferences (an organisation's OpenCode profile, ai.opencode.managed.plist)
// load above SPECTUNER_CONFIG_CONTENT. Under SpecTuner's lock-down the server must not read them.
test("the locked-down server skips machine-wide config and macOS managed preferences", () => {
  expect(configOptions({ SPECTUNER_LOCKDOWN: "1", SPECTUNER_CONFIG_CONTENT: "{}" })).toMatchObject({
    system: false,
    content: "{}",
  })
  expect(configOptions({ SPECTUNER_LOCKDOWN: "true" }).system).toBe(false)
})

test("without the lock-down the server keeps its usual config sources", () => {
  expect("system" in configOptions({})).toBe(false)
  expect(configOptions({ SPECTUNER_DISABLE_PROJECT_CONFIG: "1", SPECTUNER_DISABLE_CLAUDE_CODE: "true" })).toMatchObject({
    project: false,
    claude: false,
  })
  expect(configOptions({})).toMatchObject({ project: true, claude: true })
})

test("the server process takes its config sources from configOptions", async () => {
  const source = await Bun.file(new URL("../src/server-process.ts", import.meta.url)).text()
  expect(source).toContain("config: configOptions(process.env)")
})
