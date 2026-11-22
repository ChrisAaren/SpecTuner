// A stand-in for spectuner-sidecar.exe, compiled to an exe by `fakeSidecar()` in ./sidecar.ts.
// Usage matches the real one: `fake-sidecar.exe <command>` with the JSON request on stdin.
// FAKE_SIDECAR_LOG: append one JSON line per call: { command, request, pid }.
// FAKE_SIDECAR_MODE: "hang" never answers, "garbage" prints non-JSON and exits 3, "ledger" answers a game with
// five changes (setup, a live mod, a live setting, one undone), the mods map, a past list, and real `revert` and
// `reapply` answers, "ledger-no-mods" the same from an older helper without the mods map, "unsupported" answers an
// IL2CPP refusal, otherwise canned envelopes.
import { appendFileSync } from "node:fs"
import path from "node:path"

const command = process.argv[2] ?? ""
const request = JSON.parse(await Bun.stdin.text())
if (process.env.FAKE_SIDECAR_LOG)
  appendFileSync(process.env.FAKE_SIDECAR_LOG, `${JSON.stringify({ command, request, pid: process.pid })}\n`)

const mode = process.env.FAKE_SIDECAR_MODE
if (mode === "hang") await new Promise(() => setInterval(() => {}, 1000))
if (mode === "garbage") {
  console.log("this is not json")
  console.error("fake sidecar crashed on purpose")
  process.exit(3)
}

const respond = (envelope: Record<string, unknown>) => console.log(JSON.stringify({ command, ...envelope }))

if (mode === "unsupported") {
  if (command === "detect")
    respond({
      ok: false,
      error: { code: "IL2CPP_NOT_SUPPORTED", message: "This game is built in a way SpecTuner cannot change yet." },
      data: { supported: false, game: { name: "Locked Game" } },
    })
  if (command === "changes") respond({ ok: true, summary: "No changes.", data: { changes: [], canRestore: false } })
  process.exit(0)
}

// "ledger-replaced": a mod built twice. The first build stays "active" in the ledger and is also listed as past.
if (mode === "ledger-replaced") {
  const build = (id: string, hour: string) => ({
    id,
    kind: "mod",
    title: "Teleport",
    description: "Teleport.",
    createdAt: `2026-11-21T${hour}:00:00Z`,
    status: "active",
    verified: true,
    files: [],
  })
  if (command === "detect")
    respond({ ok: true, summary: "This is a Unity game SpecTuner can work with.", data: { supported: true, game: { name: "Ledger Game" } } })
  if (command === "changes")
    respond({
      ok: true,
      summary: "2 changes.",
      data: {
        changes: [build("c0008", "08"), build("c0005", "05")],
        total: 2,
        truncated: false,
        canRestore: true,
        mods: { c0008: { name: "teleport", build: 2 }, c0005: { name: "teleport", build: 1 } },
        past: [
          {
            id: "c0005",
            kind: "mod",
            title: "Teleport",
            description: "Teleport.",
            createdAt: "2026-11-21T05:00:00Z",
            endedAt: "2026-11-21T08:00:00Z",
            reason: "replaced",
            canReapply: true,
            mod: { name: "teleport", build: 1 },
          },
        ],
        pastTotal: 1,
        pastTruncated: false,
      },
    })
  process.exit(0)
}

if (mode === "ledger" || mode === "ledger-no-mods") {
  const entry = (id: string, kind: string, title: string, status: string) => ({
    id,
    kind,
    title,
    description: `${title}.`,
    createdAt: `2026-11-21T0${id.slice(-1)}:00:00Z`,
    status,
    verified: true,
    files: [],
  })
  if (command === "detect")
    respond({
      ok: true,
      summary: "This is a Unity game SpecTuner can work with.",
      data: { supported: true, game: { name: "Ledger Game" }, activeChanges: 3 },
    })
  if (command === "changes")
    respond({
      ok: true,
      summary: "5 changes.",
      data: {
        changes: [
          entry("c0007", "mod", "Double the jump height", "active"),
          entry("c0004", "quality", "Lower shadow distance", "reverted"),
          entry("c0003", "quality", "Turn off anti-aliasing", "active"),
          entry("c0002", "probe", "SpecTuner probe", "active"),
          entry("c0001", "loader", "Mod loader", "pending"),
        ],
        total: 5,
        truncated: false,
        canRestore: true,
        // An unreadable value is left out by the plugin, like an unreadable past entry.
        ...(mode === "ledger" ? { mods: { c0007: { name: "DoubleJump", build: 3 }, c0003: { name: 42 } } } : {}),
        past: [
          {
            id: "c0006",
            kind: "mod",
            title: "Double the jump height",
            description: "Jumps go twice as high.",
            createdAt: "2026-11-21T06:00:00Z",
            endedAt: "2026-11-21T06:30:00Z",
            reason: "restored",
            canReapply: true,
            mod: { name: "DoubleJump", build: 2 },
            reappliedFrom: "c0001",
          },
          {
            id: "c0005",
            kind: "mod",
            title: "Double the jump height",
            description: "Jumps go twice as high.",
            createdAt: "2026-11-21T05:00:00Z",
            endedAt: "2026-11-21T05:01:00Z",
            reason: "failed_test",
            canReapply: false,
            whyNot: "This change did not load when the game started, so it cannot be put back.",
            mod: { name: "DoubleJump", build: 1 },
          },
          { kind: "quality", reason: "undone" },
          {
            id: "c0004",
            kind: "quality",
            title: "Lower shadow distance",
            description: "Lower shadow distance.",
            createdAt: "2026-11-21T04:00:00Z",
            endedAt: "2026-11-21T04:10:00Z",
            reason: "something_new",
            canReapply: true,
          },
        ],
        pastTotal: 60,
        pastTruncated: true,
      },
    })
  if (command === "revert" && request.all === true)
    respond({ ok: true, summary: "The game is back to how SpecTuner first found it.", data: { fullRestore: true } })
  if (command === "revert" && request.changeId === "c0003")
    respond({ ok: true, summary: 'Undid "Turn off anti-aliasing".', data: { fullRestore: false } })
  if (command === "revert" && request.changeId === "c0001")
    respond({
      ok: false,
      error: { code: "REVERT_BLOCKED", message: "Undo your other changes first, or restore the whole game." },
      data: {},
    })
  if (command === "reapply" && request.changeId === "c0004")
    respond({
      ok: true,
      summary: 'Put back "Lower shadow distance". Saved 1 setting; it applies the next time the game starts.',
      data: { changeId: "c0007", changed: true, reappliedFrom: "c0004" },
    })
  if (command === "reapply" && request.changeId === "c0005")
    respond({
      ok: false,
      error: {
        code: "CANNOT_REAPPLY",
        message: "This change did not load when the game started, so it cannot be put back.",
      },
      data: {},
    })
  process.exit(0)
}

if (command === "detect")
  respond({
    ok: true,
    summary: "This is a Unity game SpecTuner can work with.",
    data: {
      supported: true,
      game: { name: path.basename(request.gamePath), exe: "Fake.exe", unityVersion: "2022.3.1f1", backend: "mono" },
      loader: { installed: false, ours: false, version: null },
      probe: { installed: false },
      activeChanges: 0,
    },
  })
if (command === "changes") respond({ ok: true, summary: "No changes yet.", data: { changes: [], canRestore: false } })
if (command === "set-quality")
  respond({
    ok: false,
    error: { code: "UNKNOWN_SETTING", message: "One of those settings does not exist.", detail: "bogus" },
    data: { valid: ["shadowDistance"] },
  })
// A same-launch trial (docs/sidecar-contract.md, launch-and-benchmark): canned numbers, the tried settings echoed back.
const trial = command === "launch-and-benchmark" && request.trySettings !== undefined
if (trial)
  respond({
    ok: true,
    summary: "Trying those settings made the game about 9% faster in this run. Nothing was saved.",
    data: {
      label: request.label ?? "run",
      runId: "b0002",
      avgFps: 100.0,
      low1PctFps: 60.0,
      frameMs: { avg: 10.0, p50: 9.8, p95: 12.0, p99: 14.0, max: 20.0 },
      trial: {
        settings: request.trySettings,
        pairs: 4,
        windowSeconds: 5,
        settleSeconds: 2,
        a: { avgFps: 100.0, low1PctFps: 60.0, p50Ms: 9.8, p95Ms: 12.0, frames: 2000 },
        b: { avgFps: 109.0, low1PctFps: 66.0, p50Ms: 9.0, p95Ms: 11.0, frames: 2180 },
        pairResults: [{ aFps: 100.0, bFps: 109.0, deltaPct: 9.0 }],
        meanDeltaPct: 9.0,
        confirmed: Object.keys(request.trySettings),
        notConfirmed: [],
        verdict: "better",
        reason: "Every pair was faster with the tried settings.",
      },
    },
  })
if (!trial && !["detect", "changes", "set-quality"].includes(command))
  respond({ ok: false, error: { code: "NOT_IMPLEMENTED", message: "Not available yet." }, data: {} })
