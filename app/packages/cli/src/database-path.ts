import path from "node:path"
import { SPECTUNER_CHANNEL } from "./version"

export function databasePath(data: string) {
  const filename =
    process.env.SPECTUNER_DB ??
    (["latest", "dev", "beta", "next", "prod"].includes(SPECTUNER_CHANNEL) ||
    process.env.SPECTUNER_DISABLE_CHANNEL_DB === "1" ||
    process.env.SPECTUNER_DISABLE_CHANNEL_DB === "true"
      ? "spectuner.db"
      : `spectuner-${SPECTUNER_CHANNEL.replace(/[^a-zA-Z0-9._-]/g, "-")}.db`)
  return filename === ":memory:" ? filename : path.resolve(data, filename)
}
