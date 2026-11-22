import type en from "@/runtime/i18n/en"
import type { dict } from "@spectuner/ui/i18n/en"

// SpecTuner. Upstream copy that SpecTuner words differently on the screens a player sees. The
// language module applies these over every locale, so they read like new English strings that have no translation
// yet (the runtime English fallback). Upstream's own English stays untouched in its files. The desktop's native
// menus and recovery dialogs take their copy from the same dictionary once the window has loaded.

export const overrides = {
  // Product name.
  "desktop.menu.app": "SpecTuner",
  "desktop.menu.ariaLabel": "SpecTuner menu",
  "desktop.recovery.loadFailed": "SpecTuner failed to load",
  "desktop.recovery.terminated": "SpecTuner window terminated unexpectedly",
  "desktop.recovery.unresponsive": "SpecTuner is not responding",
  "dialog.provider.title": "Connect a model",
  "dialog.provider.description": "Connect a model provider so SpecTuner can work on your game.",
  "provider.connect.apiKey.description":
    "Enter your {{provider}} API key to connect your account and use {{provider}} models in SpecTuner.",
  "settings.general.row.colorScheme.description": "Choose whether SpecTuner follows the system, light, or dark theme",
  "settings.general.row.theme.description": "Customise how SpecTuner is themed.",
  // Games, not projects; no OpenCode Go or Console upsell in the connect command.
  "home.projects": "Games",
  "home.project.add": "Add a game",
  "command.project.open": "Choose your game's folder",
  "session.new.project.add": "Choose your game's folder",
  "command.provider.connect.description": "Connect a model provider",
  // With no model connected there is no default model: the composer asks for one.
  "home.providerTip": "Connect a model so SpecTuner can work on your game",
  "prompt.toast.modelAgentRequired.title": "Connect a model first",
  "prompt.toast.modelAgentRequired.description":
    "SpecTuner needs an AI model to work on your game. Connect one, then send your request again.",
  "ui.promptInput.placeholder.normal": "What would you like to change in this game?",
  "ui.promptInput.placeholder.followUp": "Add to your request…",
} satisfies Partial<Record<keyof typeof en | keyof typeof dict, string>>
