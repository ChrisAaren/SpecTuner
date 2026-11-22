import { Option, Schema } from "effect"

declare const SPECTUNER_VERSION: string
declare const SPECTUNER_CHANNEL: string
declare const SPECTUNER_ARTIFACT: string
declare const SPECTUNER_RELEASE_HIGHLIGHTS: string

const version = typeof SPECTUNER_VERSION === "string" ? SPECTUNER_VERSION : "local"
const channel = typeof SPECTUNER_CHANNEL === "string" ? SPECTUNER_CHANNEL : "local"
const artifact = typeof SPECTUNER_ARTIFACT === "string" ? SPECTUNER_ARTIFACT : "cli"
// Stable release builds embed the release notes' Highlights; every other build has none.
const highlights =
  typeof SPECTUNER_RELEASE_HIGHLIGHTS === "string"
    ? Option.getOrElse(
        Schema.decodeUnknownOption(
          Schema.fromJsonString(Schema.Array(Schema.Struct({ title: Schema.String, body: Schema.String }))),
        )(SPECTUNER_RELEASE_HIGHLIGHTS),
        () => [],
      )
    : []

export {
  version as SPECTUNER_VERSION,
  channel as SPECTUNER_CHANNEL,
  artifact as SPECTUNER_ARTIFACT,
  highlights as SPECTUNER_HIGHLIGHTS,
}
export const SPECTUNER_LOCAL = channel === "local"
