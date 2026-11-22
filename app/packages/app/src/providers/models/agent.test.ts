import { expect, test } from "bun:test"
import { defaultAgentName, hasCustomAgent, resolveAgent } from "./agent"

test("hasCustomAgent detects only explicitly custom agents", () => {
  expect(hasCustomAgent([{ native: true }, { native: false }])).toBe(true)
  expect(hasCustomAgent([{ native: true }, {}])).toBe(false)
})

const agents = [{ name: "plan" }, { name: "build" }, { name: "custom" }]

const rows: { name: string; agents: { name: string }[]; requested?: string; expected: string }[] = [
  { name: "the requested available agent", agents, requested: "custom", expected: "custom" },
  { name: "build without a request", agents, requested: undefined, expected: "build" },
  { name: "build for a missing agent", agents, requested: "missing", expected: "build" },
  {
    name: "the first agent when build is unavailable",
    agents: [{ name: "custom" }],
    requested: "missing",
    expected: "custom",
  },
]

test.each(rows)("resolveAgent uses $name", (row) => {
  expect(resolveAgent(row.agents, row.requested)?.name).toBe(row.expected)
})

// A session nobody chose an agent for uses the server's `default_agent`, whether or not the picker is shown.
const defaults: {
  name: string
  agents: { name: string }[]
  configured?: string
  visible: boolean
  expected: string
}[] = [
  {
    name: "the server default with the picker hidden",
    agents,
    configured: "custom",
    visible: false,
    expected: "custom",
  },
  { name: "the server default with the picker shown", agents, configured: "custom", visible: true, expected: "custom" },
  { name: "build with the picker hidden and no default", agents, visible: false, expected: "build" },
  { name: "the first agent with the picker shown and no default", agents, visible: true, expected: "plan" },
  {
    name: "the only agent when the server disabled build and names it default",
    agents: [{ name: "modder" }],
    configured: "modder",
    visible: false,
    expected: "modder",
  },
  {
    name: "build when the server default is not available",
    agents,
    configured: "missing",
    visible: false,
    expected: "build",
  },
]

test.each(defaults)("a session without a chosen agent uses $name", (row) => {
  expect(resolveAgent(row.agents, defaultAgentName(row.agents, row.configured, row.visible))?.name).toBe(row.expected)
})
