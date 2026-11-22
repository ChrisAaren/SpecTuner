import { NodeServices } from "@effect/platform-node"
import { expect, test } from "bun:test"
import { Effect, Exit } from "effect"
import { Command } from "effect/cli"
import { Commands } from "../src/commands/commands"

test.each([
  { args: [], cors: [] },
  { args: ["--cors", "https://app.example.com"], cors: ["https://app.example.com"] },
  {
    args: ["--service", "--cors", "http://192.0.2.10:3001", "--cors", "https://app.example.com"],
    cors: ["http://192.0.2.10:3001", "https://app.example.com"],
  },
])("serve parses CORS flags: $args", async ({ args, cors }) => {
  const received: (readonly string[])[] = []
  const command = Commands.commands.serve.spec.pipe(
    Command.withHandler((input) => Effect.sync(() => void received.push(input.cors))),
  )
  await Effect.runPromise(Command.runWith(command, { version: "test" })(args).pipe(Effect.provide(NodeServices.layer)))
  expect(received).toEqual([cors])
})

test.each([{ args: ["--cors"] }, { args: ["--cors", ""] }])(
  "serve rejects a missing or empty CORS flag value: $args",
  async ({ args }) => {
    const command = Commands.commands.serve.spec.pipe(Command.withHandler(() => Effect.void))
    const result = await Effect.runPromise(
      Command.runWith(command, { version: "test", renderErrors: false })(args).pipe(
        Effect.exit,
        Effect.provide(NodeServices.layer),
      ),
    )
    expect(Exit.isFailure(result)).toBe(true)
  },
)
