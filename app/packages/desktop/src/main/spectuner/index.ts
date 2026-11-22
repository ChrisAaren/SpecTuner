import path from "node:path"
import { fileURLToPath } from "node:url"
import { app } from "electron"
import { SpecTunerServerEnvironment } from "./server-environment"
import { SpecTunerUpgrade } from "./upgrade"

let server: SpecTunerServerEnvironment.Server | undefined

let inTestRoot = false

/**
 * Applies the SpecTuner server environment to this process, whose environment the background service inherits. The
 * first call (from `configureApplication`, after userData is set and before the sidecar probe) resolves the server and
 * pins its port; later calls re-apply it after upstream code edited the environment (`preferApplicationEnvironment`
 * deletes `XDG_STATE_HOME` on Windows).
 */
export function applySpecTunerEnvironment(testRoot?: string) {
  if (!server) inTestRoot = testRoot !== undefined
  server ??= start(testRoot)
  SpecTunerServerEnvironment.apply(process.env, server)
}

/**
 * Variables to add to the background service's environment when the desktop starts it (macOS and Linux: SpecTuner's
 * `XDG_*` folders, which the desktop keeps out of its own environment). `undefined` on Windows.
 */
export function serverSpawnEnv() {
  return server ? SpecTunerServerEnvironment.spawnEnv(process.env, server) : undefined
}

/** The service's registration file when the client cannot find it from the desktop's environment (macOS and Linux). */
export function serverRegistrationFile() {
  return server ? SpecTunerServerEnvironment.registrationFile(server) : undefined
}

/** The server's log folder, for exported diagnostics. */
export function serverLogDir() {
  return server ? path.join(SpecTunerServerEnvironment.dataDir(server), "spectuner", "log") : undefined
}

function start(testRoot: string | undefined) {
  const resolved = SpecTunerServerEnvironment.resolve(
    { packaged: app.isPackaged, resources: resources(), testRoot },
    process.env,
  )
  SpecTunerServerEnvironment.pinPort(resolved)
  return resolved
}

// Packaged: outside app.asar, next to the bundled CLI. Unpackaged: the folder `script/stage-spectuner.ts` fills, found
// from the main bundle's directory (`out/main`) as `DesktopPaths` finds the other development resources.
function resources() {
  if (app.isPackaged) return path.join(process.resourcesPath, "spectuner")
  return path.join(path.dirname(fileURLToPath(import.meta.url)), "../../resources/spectuner")
}

/**
 * The one-time step for players coming from the build that used opencode's names (`upgrade.ts`). Runs after
 * `applySpecTunerEnvironment` (the new home is known) and the single-instance lock, and before anything reads the
 * desktop settings, opens `drafts.sqlite` or looks for a service. Never throws; a test root never migrates.
 */
export async function upgradeFromPreviousBuild() {
  // Builds with opencode's names only ever shipped for Windows, so there is nothing to carry over elsewhere.
  if (!server || inTestRoot || !SpecTunerUpgrade.applies(process.platform)) return undefined
  const newHome = server.home
  const userData = app.getPath("userData")
  const input: SpecTunerUpgrade.Input = {
    oldHome: path.join(path.dirname(newHome), app.isPackaged ? "opencode" : "opencode-dev"),
    newHome,
    userData,
    // Packaged, the old build ran the server it staged under userData; unpackaged, the one in desktop/resources.
    serverDirs: app.isPackaged ? [path.join(userData, "cli")] : [path.join(userData, "cli"), path.dirname(resources())],
  }
  const log = (message: string, data?: Record<string, unknown>) => console.info(message, data ?? "")

  try {
    const report = await SpecTunerUpgrade.run(input, SpecTunerUpgrade.defaultDeps(log))

    if (report.status === "ran") log("upgrade from the previous build finished", { complete: report.complete })

    return report
  } catch (error) {
    console.error("upgrade from the previous build failed", error)

    return undefined
  }
}
