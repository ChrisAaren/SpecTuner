import { app } from "electron"

type Channel = "local" | "dev" | "beta" | "prod"

const raw = import.meta.env.SPECTUNER_CHANNEL

export const CHANNEL: Channel = raw === "local" || raw === "dev" || raw === "beta" || raw === "prod" ? raw : "dev"

export const VERSION = app.isPackaged ? app.getVersion() : (process.env.SPECTUNER_VERSION ?? app.getVersion())

// SpecTuner's own identity, so userData, the single-instance lock, the taskbar identity and the install directory never
// meet an installed OpenCode's. Keep in sync with APP_IDS and productName in electron-builder.config.ts.
const appNames: Record<string, string> = {
  dev: "SpecTuner Dev",
  beta: "SpecTuner Beta",
  prod: "SpecTuner",
}

const appIDs: Record<string, string> = {
  dev: "com.spectuner.desktop.dev",
  beta: "com.spectuner.desktop.beta",
  prod: "com.spectuner.desktop",
}

// Local renderer/server mode keeps the dev application identity.
export const APP_NAME = app.isPackaged ? appNames[CHANNEL] : "SpecTuner Dev"

export const APP_ID = app.isPackaged ? appIDs[CHANNEL] : "com.spectuner.desktop.dev"
