import { chmod, copyFile, mkdtemp, rm } from "node:fs/promises"
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

// SpecTuner ships for Windows x64, Linux x64 and macOS on Apple Silicon. x64 builds bundle the server program's
// baseline variant, which runs on processors without AVX2.
export const CLI_BINARIES: Array<{ target: string; package: string; os: string; cpu: string }> = [
  {
    target: "windows-x64",
    package: "@spectuner/cli-windows-x64-baseline",
    os: "win32",
    cpu: "x64",
  },
  {
    target: "linux-x64",
    package: "@spectuner/cli-linux-x64-baseline",
    os: "linux",
    cpu: "x64",
  },
  {
    target: "darwin-arm64",
    package: "@spectuner/cli-darwin-arm64",
    os: "darwin",
    cpu: "arm64",
  },
]

export function nativeTarget(platform: string = process.platform, arch: string = process.arch) {
  if (platform === "win32" && arch === "x64") return "windows-x64"

  if (platform === "linux" && arch === "x64") return "linux-x64"

  if (platform === "darwin" && arch === "arm64") return "darwin-arm64"
  throw new Error(`SpecTuner builds for Windows x64, Linux x64 and macOS on Apple Silicon, not ${platform}/${arch}`)
}

/** The server program's file name in the app's resources: `spectuner-server.exe` on Windows, no extension elsewhere. */
export function serverFile(os: string) {
  return os === "win32" ? "spectuner-server.exe" : "spectuner-server"
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
  const cli = getCurrentCli()
  await copyFile(join(pkg, "bin", cli.os === "win32" ? "spectuner.exe" : "spectuner"), dest)
  await prepareCli(dest)

  const manifest = Option.getOrUndefined(
    Schema.decodeUnknownOption(Manifest)(await Bun.file(join(pkg, "package.json")).text()),
  )

  if (!manifest) throw new Error(`Bundled CLI package has no version: ${pkg}`)
  await Bun.write(versionFile(dest), manifest.version)
}

export function versionFile(cli: string) {
  return join(dirname(cli), "spectuner-server.version")
}

// Executable off Windows, and on a macOS host signed again ad hoc, as the starting code base did: Apple Silicon refuses
// to run a binary without a valid signature, and SpecTuner has no signing identity.
async function prepareCli(dest: string) {
  const { $ } = await import("bun")

  if (process.platform !== "win32") await chmod(dest, 0o755)

  if (process.platform === "darwin") await $`codesign --force --sign - ${dest}`
}

export function windowsify(path: string) {
  if (path.endsWith(".exe")) return path

  return `${path}${process.platform === "win32" ? ".exe" : ""}`
}
