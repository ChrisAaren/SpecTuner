export function hasCustomAgent(items: Array<{ native?: boolean }>) {
  return items.some((item) => item.native === false)
}

/**
 * The agent a session uses when none was chosen: the server's `default_agent`, else the first agent while the picker
 * is shown, else `build`. `resolveAgent` falls back from a name that is not available.
 */
export function defaultAgentName(items: { name: string }[], configured: string | undefined, visible: boolean) {
  return configured ?? (visible ? items[0]?.name : "build")
}

export function resolveAgent<T extends { name: string }>(items: T[], name?: string) {
  return items.find((item) => item.name === name) ?? items.find((item) => item.name === "build") ?? items[0]
}
