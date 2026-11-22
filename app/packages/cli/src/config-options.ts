import { ServerLockdown } from "@spectuner/server/middleware/lockdown"

/**
 * The server's config sources, from the environment it was started with. Under SpecTuner's lock-down
 * (`SPECTUNER_LOCKDOWN=1`, always set by the app) the machine-wide config directory and macOS managed preferences are
 * skipped: they load above `SPECTUNER_CONFIG_CONTENT`, and on macOS the managed preferences are an organisation's
 * profile for OpenCode (`ai.opencode.managed.plist`), so either could change what the modder agent may do.
 */
export function configOptions(env: Readonly<Record<string, string | undefined>> = process.env) {
  return {
    directory: env.SPECTUNER_CONFIG_DIR,
    project: !truthy(env.SPECTUNER_CONFIG_PROJECT_DISABLE ?? env.SPECTUNER_DISABLE_PROJECT_CONFIG),
    // Config reads .claude only for skills; CLAUDE.md is gated under instructions.
    claude: !(truthy(env.SPECTUNER_DISABLE_CLAUDE_CODE) || truthy(env.SPECTUNER_DISABLE_CLAUDE_CODE_SKILLS)),
    ...(ServerLockdown.enabled(env) ? { system: false } : {}),
    file: env.SPECTUNER_CONFIG,
    content: env.SPECTUNER_CONFIG_CONTENT,
  }
}

function truthy(value?: string) {
  return value === "1" || value?.toLowerCase() === "true"
}
