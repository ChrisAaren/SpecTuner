import { Global } from "@spectuner/util/global"
import { SPECTUNER_CHANNEL, SPECTUNER_VERSION } from "../version"
import { Hash } from "@spectuner/util/hash"
import { Service } from "@spectuner/client/effect/service"
import { ServerLockdown } from "@spectuner/server/middleware/lockdown"
import { Effect, FileSystem, Option, Schedule, Schema } from "effect"
import { randomBytes } from "crypto"
import path from "path"
import { selfCommand } from "../util/process"

// The CLI's service configuration file, plus the Service.EnsureOptions binding that
// points the client package's service operations at this CLI: which
// registration file (by channel), which version, and how to spawn spectuner.

export const Info = Schema.Struct({
  disabled: Schema.optional(Schema.Boolean),
  hostname: Schema.optional(Schema.String),
  port: Schema.optional(Schema.Int.check(Schema.isGreaterThanOrEqualTo(1), Schema.isLessThanOrEqualTo(65_535))),
  password: Schema.optional(Schema.String),
  cors: Schema.optional(Schema.Array(Schema.String)),
  env: Schema.optional(Schema.Record(Schema.String, Schema.String)),
})
export type Info = typeof Info.Type

const decodeInfo = Schema.decodeUnknownEffect(Schema.fromJsonString(Info))
const decodeRegistration = Schema.decodeUnknownEffect(Schema.fromJsonString(Service.Info))

export function filename(channel = SPECTUNER_CHANNEL) {
  if (channel === "latest" || channel === "dev" || channel === "beta" || channel === "next") return "service.json"
  return `service-${channel.replace(/[^a-zA-Z0-9._-]/g, "-")}.json`
}

export function defaultPort(channel = SPECTUNER_CHANNEL, platform = process.platform) {
  const port = channelPort(channel)
  // Windows hands out 0xC000-0xFFFF for outgoing connections, Hyper-V and WinNAT reserve blocks of that range at boot,
  // and WSL mirrored networking reserves a 4096-port block that usually covers 44620-48715. A reserved port fails as in
  // use while nothing listens on it. Moving into the 0x7000 block, below 32768, avoids all of them and keeps the low
  // 12 bits: 0xC0DE becomes 0x70DE.
  if (platform === "win32" && port >= 0x8000) return 0x7000 | (port & 0xfff)
  return port
}

function channelPort(channel: string) {
  if (channel === "latest" || channel === "dev" || channel === "beta" || channel === "next") return 0xc0de
  if (channel === "local") return 0xc0df
  return 10_000 + (Number.parseInt(Hash.fast(channel).slice(0, 8), 16) % 50_000)
}

export function legacyFilename(channel = SPECTUNER_CHANNEL) {
  if (channel === "latest" || channel === "local") return
  return `service-${Hash.fast(channel)}.json`
}

export function versionBelongsToChannel(
  version: string | undefined,
  channel = SPECTUNER_CHANNEL,
  installedVersion = SPECTUNER_VERSION,
) {
  if (version === undefined) return false
  if (version === installedVersion) return true
  const prefix = `0.0.0-${channel}-`
  if (!version.startsWith(prefix)) return false
  return /^\d+(?:\.\d+)?$/.test(version.slice(prefix.length))
}

export const migrateRegistration = Effect.fnUntraced(function* (
  legacy: string,
  file: string,
  channel = SPECTUNER_CHANNEL,
  installedVersion = SPECTUNER_VERSION,
) {
  const fs = yield* FileSystem.FileSystem
  const text = yield* fs.readFileString(legacy).pipe(Effect.option)
  if (Option.isNone(text)) return
  const registration = yield* decodeRegistration(text.value).pipe(Effect.option)
  if (Option.isNone(registration)) return
  if (!versionBelongsToChannel(registration.value.version, channel, installedVersion)) return
  yield* fs.writeFileString(file, text.value, { flag: "wx", mode: 0o600 }).pipe(Effect.ignore)
})

export const migrateConfig = Effect.fnUntraced(function* (legacy: string, file: string) {
  const fs = yield* FileSystem.FileSystem
  const text = yield* fs.readFileString(legacy).pipe(Effect.option)
  if (Option.isNone(text)) return
  if (Option.isNone(yield* decodeInfo(text.value).pipe(Effect.option))) return
  yield* fs.writeFileString(file, text.value, { flag: "wx", mode: 0o600 }).pipe(Effect.ignore)
})

const paths = Effect.gen(function* () {
  const fs = yield* FileSystem.FileSystem
  const global = yield* Global.Service
  const name = filename()
  const legacy = legacyFilename()
  const file = path.join(global.state, name)
  return {
    fs,
    file,
    legacyConfigFile: legacy ? path.join(global.config, legacy) : undefined,
    legacyRegistrationFiles: [
      ...(legacy ? [path.join(global.state, legacy)] : []),
      ...(name !== "service.json" && SPECTUNER_CHANNEL !== "local" ? [path.join(global.state, "service.json")] : []),
    ],
    configFile: path.join(global.config, name),
  }
})

// Clients find the service at its registration under XDG_STATE_HOME. A saved value would move the
// registration away from them, and children of the service (tool shells, a relaunched Desktop) would
// look for it in the wrong place, so the service keeps the state directory it was launched with.
//
// Under SpecTuner's lock-down (started with SPECTUNER_LOCKDOWN=1), the starting environment's SPECTUNER_* and XDG_*
// variables are what lock the server down (packages/modder/src/env.ts sets them and drops every other one), so the
// file may change none of them, in any letter case: it cannot switch the lock-down off, load other config or data, or
// turn flags back on.
export function processEnv(env: Info["env"], base: Readonly<Record<string, string | undefined>> = process.env) {
  if (env === undefined) return undefined
  const { XDG_STATE_HOME: _state, ...rest } = env
  if (!ServerLockdown.enabled(base)) return rest
  return Object.fromEntries(Object.entries(rest).filter(([key]) => !/^(SPECTUNER_|XDG_)/i.test(key)))
}

export const options = Effect.fnUntraced(function* (input: { readonly checkVersion?: boolean } = {}) {
  const { file, legacyRegistrationFiles } = yield* paths
  yield* Effect.forEach(legacyRegistrationFiles, (legacy) => migrateRegistration(legacy, file))
  return {
    file,
    version: input.checkVersion ? SPECTUNER_VERSION : undefined,
    env: processEnv((yield* read()).env),
    command: [...selfCommand(), "serve", "--service"],
  }
})

export const read = Effect.fn("cli.service-config.read")(function* () {
  const { fs, configFile, legacyConfigFile } = yield* paths
  if (legacyConfigFile) yield* migrateConfig(legacyConfigFile, configFile)
  const text = yield* fs.readFileString(configFile).pipe(Effect.option)
  if (Option.isNone(text)) return {} as Info
  const info = yield* decodeInfo(text.value).pipe(Effect.option)
  if (Option.isSome(info)) return info.value
  return {} as Info
})

const write = Effect.fn("cli.service-config.write")(function* (value: Info) {
  const { fs, configFile } = yield* paths
  const temp = configFile + "." + process.pid + ".tmp"
  yield* fs.makeDirectory(path.dirname(configFile), { recursive: true })
  yield* fs.writeFileString(temp, JSON.stringify(value, null, 2) + "\n", { mode: 0o600 })
  yield* fs.rename(temp, configFile).pipe(Effect.retry({ times: 20, schedule: Schedule.spaced("10 millis") }))
})

export const password = Effect.fn("cli.service-config.password")(function* (value?: string) {
  const existing = yield* read()
  if (value === undefined && existing.password) return existing.password
  const next = value ?? randomBytes(32).toString("base64url")

  // Keep one private credential across server restarts so discovered clients
  // can reconnect without exposing a password flag or environment variable.
  yield* write({ ...existing, password: next })
  return next
})

export * as ServiceConfig from "./service-config"
