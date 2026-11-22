export * as SpecTunerAgents from "./agents.ts"

import type { AgentEditor } from "@spectuner/plugin/promise/agent"
import type { Context } from "@spectuner/plugin/promise/plugin"
import { SpecTunerTools } from "./tools.ts"

export const MODDER = "modder"
/** Hidden agents the server itself uses for titles, compaction and summaries. They keep no tools. */
export const HELPERS = ["title", "compaction", "summary"]

const DENY_ALL = { action: "*", resource: "*", effect: "deny" } as const
const GAME_TOOLS: readonly string[] = SpecTunerTools.names

export const allowed = (id: string) => id === MODDER || HELPERS.includes(id)

/**
 * Leaves the modder as the only agent a session can use. Any other agent, whether built in or added by a config file
 * (including the player's writable one), is removed. The modder's rules end with deny-all plus one allow per game
 * tool, and the helper agents end with deny-all, so no rule a config file adds before them can widen them.
 */
export function lock(editor: AgentEditor) {
  editor
    .list()
    .filter((agent) => !allowed(agent.id))
    .forEach((agent) => editor.remove(agent.id))
  // `update` would create a missing agent; the modder itself (prompt and all) comes from the config document.
  if (editor.get(MODDER))
    editor.update(MODDER, (agent) => {
      agent.mode = "primary"
      agent.hidden = false
      agent.permissions.push(
        DENY_ALL,
        ...GAME_TOOLS.map((action) => ({ action, resource: "*", effect: "allow" as const })),
      )
    })
  HELPERS.forEach((id) => {
    if (editor.get(id)) editor.update(id, (agent) => void agent.permissions.push(DENY_ALL))
  })
  editor.default(MODDER)
}

/**
 * Installs the lock-down for one Location and returns its cleanup:
 * - Every model request (and compaction request) of a session using the modder carries only the game tools; any other
 *   agent's requests carry none. This holds even if some agent slipped past the agent lock.
 * - The agent lock above. Transforms run in the order they were registered, and plugins register theirs as they load,
 *   so the config's agents can land after the lock. Whenever agents change and a foreign one is present, the lock is
 *   registered again so it runs last; config hot-reloads only rerun existing transforms, which keeps it last.
 */
export async function install(ctx: Context) {
  const filter = (event: { agent: string; tools: Record<string, unknown> }) => {
    Object.keys(event.tools)
      .filter((name) => event.agent !== MODDER || !GAME_TOOLS.includes(name))
      .forEach((name) => delete event.tools[name])
  }
  await ctx.session.hook("context", filter)
  await ctx.session.hook("compaction", filter)

  const state = { registration: await ctx.agent.transform(lock), attempts: 0 }
  const controller = new AbortController()
  const check = async () => {
    const agents = await ctx.agent.list({ location: { directory: ctx.location.directory } })
    if (agents.data.every((agent) => allowed(agent.id))) {
      state.attempts = 0
      return
    }
    // Re-registering changes the agents again; give up after a few rounds rather than loop if the lock cannot win.
    if (state.attempts >= 3) return
    state.attempts++
    const previous = state.registration
    state.registration = await ctx.agent.transform(lock)
    await previous.dispose()
  }
  // `server.connected` always arrives first, so this also checks once at start.
  void (async () => {
    for await (const _event of ctx.event.subscribe({ types: ["agent.updated"], signal: controller.signal }))
      await check().catch(() => {})
  })().catch(() => {})
  return () => controller.abort()
}
