export const available = true

export async function resolveBinary() {
  return process.env.SPECTUNER_PTY_BIN || "opencode-pty"
}
