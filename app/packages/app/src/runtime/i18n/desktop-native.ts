import { flow, Option, Schema } from "effect"

// SpecTuner is English only: a player on any Windows language sees one consistent app. To offer another language, add
// its code here with its label and tag, its dictionaries (`app/src/runtime/i18n/<code>.ts`,
// `ui/src/i18n/<code>.ts`) and a loader in `language.tsx`.
export const DESKTOP_NATIVE_LOCALES = ["en"] as const

export type DesktopNativeLocale = (typeof DESKTOP_NATIVE_LOCALES)[number]

export const DESKTOP_NATIVE_LABELS: Record<DesktopNativeLocale, string> = {
  en: "English",
}

export const DESKTOP_NATIVE_LOCALE_TAGS: Record<DesktopNativeLocale, string> = {
  en: "en",
}

export function detectDesktopNativeLocale(languages: readonly string[]): DesktopNativeLocale {
  for (const language of languages) {
    const source = locale(language)

    if (!source) continue

    const match = DESKTOP_NATIVE_LOCALES.find((candidate) => {
      const target = locale(DESKTOP_NATIVE_LOCALE_TAGS[candidate])

      return target?.language === source.language && normalizeScript(target.script) === normalizeScript(source.script)
    })

    if (match) return match
  }

  return "en"
}

function locale(value: string) {
  try {
    return new Intl.Locale(value).maximize()
  } catch {
    return undefined
  }
}

// Bun's ICU currently reports Punjabi's Arabic script as the invalid "Aran" code.
const normalizeScript = (script?: string) => (script === "Aran" ? "Arab" : script)

export const DESKTOP_NATIVE_ENGLISH = {
  "desktop.menu.app": "SpecTuner",
  "desktop.menu.file": "File",
  "desktop.menu.edit": "Edit",
  "desktop.menu.view": "View",
  "desktop.menu.go": "Go",
  "desktop.menu.window": "Window",
  "desktop.menu.help": "Help",
  "desktop.menu.settings": "Settings",
  "desktop.menu.reloadWebview": "Reload Webview",
  "desktop.menu.restart": "Restart",
  "desktop.menu.exportLogs": "Export Logs…",
  "desktop.menu.newSession": "New Session",
  "desktop.menu.openProject": "Open Project…",
  "desktop.menu.newWindow": "New Window",
  "desktop.menu.closeWindow": "Close Window",
  "desktop.menu.undo": "Undo",
  "desktop.menu.redo": "Redo",
  "desktop.menu.cut": "Cut",
  "desktop.menu.copy": "Copy",
  "desktop.menu.paste": "Paste",
  "desktop.menu.delete": "Delete",
  "desktop.menu.selectAll": "Select All",
  "desktop.menu.toggleSidebar": "Toggle Sidebar",
  "desktop.menu.toggleFileTree": "Toggle File Tree",
  "desktop.menu.reload": "Reload",
  "desktop.menu.toggleDeveloperTools": "Toggle Developer Tools",
  "desktop.menu.actualSize": "Actual Size",
  "desktop.menu.zoomIn": "Zoom In",
  "desktop.menu.zoomOut": "Zoom Out",
  "desktop.menu.toggleFullScreen": "Toggle Full Screen",
  "desktop.menu.back": "Back",
  "desktop.menu.forward": "Forward",
  "desktop.menu.previousSession": "Previous Session",
  "desktop.menu.nextSession": "Next Session",
  "desktop.menu.previousProject": "Previous Project",
  "desktop.menu.nextProject": "Next Project",
  "desktop.menu.minimize": "Minimize",
  "desktop.menu.maximize": "Maximize",
  "desktop.menu.ariaLabel": "SpecTuner menu",

  "desktop.recovery.action.relaunch": "Relaunch",
  "desktop.recovery.action.exportLogs": "Export Logs",
  "desktop.recovery.action.keepWaiting": "Keep Waiting",
  "desktop.recovery.action.quit": "Quit",
  "desktop.recovery.loadFailed": "SpecTuner failed to load",
  "desktop.recovery.terminated": "SpecTuner window terminated unexpectedly",
  "desktop.recovery.unresponsive": "SpecTuner is not responding",
  "desktop.recovery.unresponsive.detail": "You can relaunch the app, open the logs, or keep waiting.",
  "desktop.recovery.loadFailed.detail": "Window: {{window}}\nURL: {{url}}\nError: {{code}} {{description}}",
  "desktop.recovery.terminated.detail": "Window: {{window}}\nReason: {{reason}}\nCode: {{code}}",
  "desktop.recovery.unknown": "<unknown>",

  "desktop.dialog.chooseFolder": "Choose a folder",
  "desktop.dialog.chooseFile": "Choose a file",
  "desktop.dialog.saveFile": "Save file",
  "desktop.dialog.files": "Files",
  "desktop.onboarding.defaultProject": "Default Project",

  "desktop.picker.error.notSelected": "File was not selected by the picker",
  "desktop.picker.error.sizeLimit": "Selected attachments exceed the {{limit}} MB limit",

  "desktop.contextMenu.spellcheck": "Spellcheck",
  "desktop.contextMenu.spellcheckLanguages": "Spellcheck Languages",
} as const

export type DesktopNativeKey = keyof typeof DESKTOP_NATIVE_ENGLISH

export type DesktopNativeMessages = Record<DesktopNativeKey, string>

export type DesktopNativeBundle = { locale: DesktopNativeLocale; messages: DesktopNativeMessages }

// SAFETY: DESKTOP_NATIVE_ENGLISH is a const literal, so its own keys are exactly DesktopNativeKey.
export const DESKTOP_NATIVE_KEYS = Object.keys(DESKTOP_NATIVE_ENGLISH) as DesktopNativeKey[]

export const DESKTOP_NATIVE_MAX_PAYLOAD_BYTES = 64 * 1024

export function createDesktopNativeBundle(
  locale: DesktopNativeLocale,
  translate: (key: DesktopNativeKey) => string,
): DesktopNativeBundle {
  return {
    locale,
    // SAFETY: the entries map every DesktopNativeKey to a string, which is exactly DesktopNativeMessages.
    messages: Object.fromEntries(DESKTOP_NATIVE_KEYS.map((key) => [key, translate(key)])) as DesktopNativeMessages,
  }
}

const DesktopNativeBundleSchema = Schema.Struct({
  locale: Schema.Literals(DESKTOP_NATIVE_LOCALES),
  messages: Schema.Record(Schema.Literals(DESKTOP_NATIVE_KEYS), Schema.String),
}).check(
  Schema.makeFilter(
    (bundle) => new TextEncoder().encode(JSON.stringify(bundle)).byteLength <= DESKTOP_NATIVE_MAX_PAYLOAD_BYTES,
  ),
)

const decodeDesktopNativeBundle = Schema.decodeUnknownOption(DesktopNativeBundleSchema, { onExcessProperty: "error" })

/** Validates a bundle the renderer sent over IPC: a supported locale, exactly the native keys, and a bounded size. */
export const parseDesktopNativeBundle = flow(decodeDesktopNativeBundle, Option.getOrUndefined)

export function formatDesktopNativeMessage(message: string, params?: Record<string, string | number>) {
  if (!params) return message

  return message.replace(/\{\{([^{}]+)\}\}/g, (match, key: string) => {
    const value = params[key]

    return value === undefined ? match : String(value)
  })
}
