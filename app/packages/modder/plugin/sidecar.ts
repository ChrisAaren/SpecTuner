export * as Sidecar from "./sidecar.ts"

import { spawn } from "node:child_process"
import { existsSync } from "node:fs"
import path from "node:path"
import { Option, Schema } from "effect"

/** The JSON envelope every sidecar command writes to stdout (docs/sidecar-contract.md). */
export const Envelope = Schema.Struct({
  ok: Schema.Boolean,
  command: Schema.String,
  summary: Schema.optional(Schema.String),
  error: Schema.optional(
    Schema.Struct({
      code: Schema.String,
      message: Schema.String,
      detail: Schema.optional(Schema.NullOr(Schema.String)),
    }),
  ),
  data: Schema.optional(Schema.Unknown),
})
export type Envelope = typeof Envelope.Type

const decodeEnvelope = Schema.decodeUnknownOption(Schema.fromJsonString(Envelope))

export const MINUTE = 60_000

/** Thrown when the sidecar could not be run or did not answer with an envelope. The message is model-visible. */
export class SidecarError extends Error {}

export interface RunInput {
  /**
   * Absolute path of the sidecar (`spectuner-sidecar.exe` on Windows, `spectuner-sidecar` elsewhere); usually
   * `process.env.SPECTUNER_SIDECAR`.
   */
  readonly exe: string | undefined
  readonly command: string
  readonly request: Readonly<Record<string, unknown>>
  readonly timeoutMs: number
  readonly signal?: AbortSignal
}

/**
 * Runs one sidecar command: the request goes to stdin as JSON, the envelope comes back on stdout. An envelope with
 * `ok: false` is a normal result. Only failures to run the helper or to read its answer throw.
 */
export function run(input: RunInput) {
  const exe = requireExe(input.exe)
  if (input.signal?.aborted) return Promise.reject(new SidecarError("Stopped before the helper program started."))
  return new Promise<Envelope>((resolve, reject) => {
    // Run from the helper's own folder, not the server's working directory, so nothing there can influence it.
    const child = spawn(exe, [input.command], {
      cwd: path.dirname(exe),
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    })
    const stdout: Buffer[] = []
    const stderr: Buffer[] = []
    const state = { settled: false }
    const finish = (error: Error | undefined, envelope?: Envelope) => {
      if (state.settled) return
      state.settled = true
      clearTimeout(timer)
      input.signal?.removeEventListener("abort", abort)
      if (error) return reject(error)
      if (envelope) resolve(envelope)
    }
    const stop = (message: string) => {
      child.kill()
      finish(new SidecarError(message))
    }
    const abort = () => stop("Stopped by the player before the helper program finished.")
    const timer = setTimeout(
      () =>
        stop(`The helper program did not finish within ${Math.round(input.timeoutMs / 1000)} seconds and was stopped.`),
      input.timeoutMs,
    )
    input.signal?.addEventListener("abort", abort, { once: true })
    child.stdout.on("data", (chunk: Buffer) => stdout.push(chunk))
    child.stderr.on("data", (chunk: Buffer) => stderr.push(chunk))
    child.on("error", (error) => finish(new SidecarError(`Could not start the helper program: ${error.message}`)))
    child.on("close", (code) => {
      const text = Buffer.concat(stdout).toString("utf8").trim()
      const envelope = Option.getOrUndefined(decodeEnvelope(text))
      if (envelope) return finish(undefined, envelope)
      const detail = Buffer.concat(stderr).toString("utf8").trim().slice(-800)
      finish(
        new SidecarError(
          `The helper program ended (exit code ${code}) without a readable answer.${detail ? ` Details: ${detail}` : ""}`,
        ),
      )
    })
    // The child may exit before reading its input; that shows up as an unreadable answer, not as a crash here.
    child.stdin.on("error", () => {})
    child.stdin.end(JSON.stringify(input.request))
  })
}

/** The envelope as compact text for the model: the JSON without the redundant `command` field. */
export function format(envelope: Envelope) {
  const { command: _command, ...rest } = envelope
  return JSON.stringify(rest)
}

function requireExe(exe: string | undefined) {
  if (!exe)
    throw new SidecarError(
      "The SpecTuner helper program is not configured (SPECTUNER_SIDECAR is not set), so no game tools can run. Reinstalling SpecTuner should fix this.",
    )
  if (!path.isAbsolute(exe) || !existsSync(exe))
    throw new SidecarError(
      `The SpecTuner helper program was not found at ${exe}, so no game tools can run. Reinstalling SpecTuner should fix this.`,
    )
  return exe
}
