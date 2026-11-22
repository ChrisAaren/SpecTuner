import { Flag, GlobalFlag } from "effect/cli"
import { Schema } from "effect"
import { Spec } from "../framework/spec"

export const PrintLogs = GlobalFlag.Setting("print-logs")({
  flag: Flag.Boolean("print-logs").pipe(Flag.withDescription("Print logs to stderr"), Flag.withDefault(false)),
})

declare const SPECTUNER_CLI_NAME: string | undefined

// SpecTuner's local server. The desktop app runs `serve --service` and reads `--version`; nothing else is offered.
const Root = Spec.make(typeof SPECTUNER_CLI_NAME === "string" ? SPECTUNER_CLI_NAME : "spectuner", {
  description: "SpecTuner server",
  commands: [
    Spec.make("serve", {
      description: "Start the SpecTuner server",
      params: {
        hostname: Flag.String("hostname").pipe(Flag.optional),
        port: Flag.Int("port").pipe(Flag.optional),
        cors: Flag.String("cors").pipe(
          Flag.withSchema(Schema.NonEmptyString),
          Flag.withDescription("Additional allowed CORS origin (repeat for multiple origins)"),
          Flag.atLeast(0),
        ),
        service: Flag.Boolean("service").pipe(
          Flag.withDescription("Run as the background service and register it for clients"),
          Flag.withDefault(false),
        ),
      },
    }),
  ],
})

export const Commands = Root
