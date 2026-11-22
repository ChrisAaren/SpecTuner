export * as SpecTunerDisplay from "./display.ts"

import type { Info } from "@spectuner/plugin/promise/tool"

// What a player reads in the conversation for each game tool call: a plain title while it runs (`metadata.display`,
// published through `progress`), then the sidecar's one-sentence summary or plain refusal as its result. The app's
// generic tool row shows `metadata.display` instead of the tool's name and raw arguments; those stay behind the
// row's expand. English, like the sidecar's own sentences and the modder's replies.

export interface Display {
  readonly title: string
  readonly subtitle?: string
}

/** The plain title of a game tool call, from its name and the model's input. */
export function title(tool: string, input: unknown): string {
  const field = (name: string) => {
    if (typeof input !== "object" || input === null || !(name in input)) return undefined

    const value: unknown = Object.getOwnPropertyDescriptor(input, name)?.value

    return typeof value === "string" && value.trim() ? value.trim() : value
  }

  if (tool === "game_detect") return "Checking your game"

  if (tool === "game_benchmark") {
    const tried = field("trySettings")
    if (typeof tried === "object" && tried !== null) {
      const names = Object.entries(tried).map(([key, value]) => settingName(key, value))
      return names.length > 0 ? `Trying a setting: ${names.join(", ")}` : "Trying a setting"
    }
    if (tried) return "Trying a setting"

    const label = field("label")

    return typeof label === "string" ? `Measuring performance: ${label}` : "Measuring performance"
  }

  if (tool === "game_set_quality") return withTitle("Changing a setting", field("title"))

  if (tool === "game_build_mod") return withTitle("Building your change", field("title"))

  if (tool === "game_revert") return field("all") === true ? "Restoring the game" : "Undoing a change"

  // A search shows its word ("jump"); type and member names from a read stay behind the row's expand.
  if (tool === "game_search_code") return withTitle("Looking through the game's code", field("query"))

  if (tool === "game_read_code") return "Looking through the game's code"

  if (tool === "game_read_logs") return "Reading the game's log"

  if (tool === "game_changes") return "Listing changes"

  if (tool === "game_reapply") return "Putting a change back"

  return tool
}

/**
 * The tool with a player-facing display: `progress` carries the title as soon as the call starts, and the result's
 * metadata adds the sidecar's sentence (the existing `metadata.title`) as the subtitle.
 */
export function wrap(tool: Info): Info {
  return {
    ...tool,
    execute: async (input, context) => {
      const display: Display = { title: title(tool.name, input) }

      // The row shows the title while the helper works; a failed publish only leaves the generic row.
      await context.progress({ display }).catch(() => {})
      const result = await tool.execute(input, context)
      const sentence = typeof result.metadata?.title === "string" ? result.metadata.title : undefined

      return { ...result, metadata: { ...result.metadata, display: { ...display, subtitle: sentence } } }
    },
  }
}

// Plain names for quality settings; turning a setting off reads as "<name> off".
const SETTING_NAMES: Record<string, string> = {
  renderScale: "render scale",
  shadows: "shadows",
  shadowDistance: "shadow distance",
  shadowResolution: "shadow detail",
  shadowCascades: "shadow cascades",
  antiAliasing: "edge smoothing",
  anisotropicFiltering: "texture filtering",
  textureMipmapLimit: "texture detail",
  lodBias: "distant detail",
  maximumLODLevel: "model detail",
  pixelLightCount: "light count",
  softParticles: "soft particles",
  realtimeReflectionProbes: "reflections",
}

function settingName(key: string, value: unknown) {
  const name = SETTING_NAMES[key] ?? key
  const off = value === 0 || value === false || (typeof value === "string" && /^(disable|off)$/i.test(value))
  return off && key !== "lodBias" && key !== "textureMipmapLimit" && key !== "maximumLODLevel" ? `${name} off` : name
}

function withTitle(action: string, title: unknown) {
  return typeof title === "string" ? `${action}: ${title}` : action
}
