import { copyFile, mkdtemp, rm } from "node:fs/promises"
import { tmpdir } from "node:os"
import { dirname, join } from "node:path"
import { Option, Schema } from "effect"

const CLI_VERSION = "dev"

const Manifest = Schema.fromJsonString(Schema.Struct({ version: Schema.NonEmptyString }))

export type Channel = "dev" | "beta" | "prod"

export function resolveChannel(): Channel {
  const raw = process.env.SPECTUNER_CHANNEL

  if (raw === "dev" || raw === "beta" || raw === "prod") return raw

  if (raw === "latest") return "prod"

  return "dev"
}

// SpecTuner ships for Windows x64 only.
export const CLI_BINARIES: Array<{ target: string; package: string; os: string; cpu: string }> = [
  {
    target: "windows-x64",
    package: "@spectuner/cli-windows-x64-baseline",
    os: "win32",
    cpu: "x64",
  },
]

function nativeTarget() {
  const { platform, arch } = process

  if (platform === "win32" && arch === "x64") return "windows-x64"
  throw new Error(`SpecTuner builds for Windows x64 only, not ${platform}/${arch}`)
}

export function getCurrentCli(target = process.env.SPECTUNER_TARGET ?? nativeTarget()) {
  const binaryConfig = CLI_BINARIES.find((item) => item.target === target)

  if (!binaryConfig) throw new Error(`CLI configuration not available for target '${target}'`)

  return binaryConfig
}

export async function downloadCliToResources(version = CLI_VERSION, dest = windowsify("resources/spectuner-server")) {
  const { $ } = await import("bun")
  const cli = getCurrentCli()
  const directory = await mkdtemp(join(tmpdir(), "spectuner-server-"))

  try {
    await $`bun install --no-save --cwd ${directory} ${`${cli.package}@${version}`} ${`--os=${cli.os}`} ${`--cpu=${cli.cpu}`}`
    await copyCliToResources(join(directory, "node_modules", cli.package), dest)
  } finally {
    await rm(directory, { recursive: true, force: true })
  }

  console.log(`Copied ${cli.package}@${version} to ${dest}`)
}

export async function copyBuiltCliToResources(root: string, dest = windowsify("resources/spectuner-server")) {
  const cli = getCurrentCli()
  const directory = cli.package.replace("@spectuner/", "")
  await copyCliToResources(join(root, directory), dest)
}

// The package directory is an npm package: its package.json version is the string the executable
// prints for --version. Writing it next to the executable spares the desktop a ~400 ms spawn of the
// 200 MB binary on first launch.
async function copyCliToResources(pkg: string, dest: string) {
  await copyFile(join(pkg, "bin", "spectuner.exe"), dest)

  const manifest = Option.getOrUndefined(
    Schema.decodeUnknownOption(Manifest)(await Bun.file(join(pkg, "package.json")).text()),
  )

  if (!manifest) throw new Error(`Bundled CLI package has no version: ${pkg}`)
  await Bun.write(versionFile(dest), manifest.version)
}

export function versionFile(cli: string) {
  return join(dirname(cli), "spectuner-server.version")
}

export function windowsify(path: string) {
  if (path.endsWith(".exe")) return path

  return `${path}.exe`
}
