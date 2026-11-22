import { createHash } from "node:crypto"
import { existsSync, renameSync } from "node:fs"
import os from "node:os"
import path from "node:path"

const SOURCE = path.join(import.meta.dir, "fake-sidecar.ts")

/**
 * Compiles the fake sidecar to a real exe, once per source revision, so the plugin spawns it exactly like the real
 * `spectuner-sidecar.exe` (no shell, no interpreter in between).
 */
export async function fakeSidecar() {
  const hash = createHash("sha256")
    .update(await Bun.file(SOURCE).text())
    .update(Bun.version)
    .digest("hex")
    .slice(0, 12)
  const exe = path.join(os.tmpdir(), `spectuner-fake-sidecar-${hash}${process.platform === "win32" ? ".exe" : ""}`)
  if (existsSync(exe)) return exe
  // Build under a private name and rename, so an interrupted build never leaves a broken exe behind.
  const partial = `${exe}.${process.pid}.partial${path.extname(exe)}`
  const result = Bun.spawnSync([process.execPath, "build", "--compile", SOURCE, "--outfile", partial], {
    stdout: "pipe",
    stderr: "pipe",
  })
  if (result.exitCode !== 0) throw new Error(`Could not compile the fake sidecar: ${result.stderr.toString()}`)
  renameSync(partial, exe)
  return exe
}

/** Reads the calls the fake sidecar logged. */
export async function calls(
  log: string,
): Promise<{ command: string; request: Record<string, unknown>; pid: number }[]> {
  if (!existsSync(log)) return []
  return (await Bun.file(log).text())
    .split("\n")
    .filter(Boolean)
    .map((line) => JSON.parse(line))
}
