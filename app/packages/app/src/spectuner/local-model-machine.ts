import type { SpecTunerRpc } from "@spectuner/modder/rpc"

// SpecTuner. The "Use a local model" form: type an address (and a key if the server needs one), find
// its models, pick one, save. One request at a time; answers carry the attempt that asked for them.

/** Why the form shows an error: a finding the server reported, the config file, or a request that failed. */
export type LocalModelFailure = SpecTunerRpc.LocalFindFailure | "invalid-model" | "unreadable" | "request"

export type LocalModelPhase =
  | { readonly type: "editing"; readonly failure?: LocalModelFailure }
  | { readonly type: "finding"; readonly attempt: number }
  | {
      readonly type: "found"
      readonly address: string
      readonly models: readonly SpecTunerRpc.LocalFind["models"][number][]
      readonly selected: string
      readonly failure?: LocalModelFailure
    }
  | {
      readonly type: "saving"
      readonly attempt: number
      readonly address: string
      readonly models: readonly SpecTunerRpc.LocalFind["models"][number][]
      readonly selected: string
    }
  | { readonly type: "saved"; readonly model: string }

export interface LocalModelState {
  readonly address: string
  readonly key: string
  readonly phase: LocalModelPhase
  readonly attempts: number
}

export type LocalModelEvent =
  | { readonly type: "address.changed"; readonly value: string }
  | { readonly type: "key.changed"; readonly value: string }
  | { readonly type: "find" }
  | { readonly type: "found"; readonly attempt: number; readonly result: SpecTunerRpc.LocalFind }
  | { readonly type: "model.selected"; readonly model: string }
  | { readonly type: "save" }
  | { readonly type: "saved"; readonly attempt: number; readonly result: SpecTunerRpc.LocalResult }
  | { readonly type: "request.failed"; readonly attempt: number }

export type LocalModelEffect =
  | { readonly type: "find"; readonly attempt: number; readonly address: string; readonly key: string | undefined }
  | {
      readonly type: "save"
      readonly attempt: number
      readonly address: string
      readonly key: string | undefined
      readonly model: string
      readonly context: number | null
    }
  /** The model is saved: refresh the model list and choose it for the next message. */
  | { readonly type: "use"; readonly model: string }

export interface LocalModelTransition {
  readonly state: LocalModelState
  readonly effects: readonly LocalModelEffect[]
}

const NONE: readonly LocalModelEffect[] = []

/** The context window the server assumes when a local server reports none (`DEFAULT_CONTEXT` there). */
export const ASSUMED_CONTEXT = 8192

/** Below this window, a game change that needs the model to read game code often runs out of room. */
export const SMALL_CONTEXT = 16_000

/** Whether a model's window (or the assumed one, when the server reports none) is small enough to warn about. */
export function isSmallContext(context: number | null) {
  return (context ?? ASSUMED_CONTEXT) < SMALL_CONTEXT
}

export function createLocalModelState(address = ""): LocalModelState {
  return { address, key: "", phase: { type: "editing" }, attempts: 0 }
}

export function transitionLocalModel(state: LocalModelState, event: LocalModelEvent): LocalModelTransition {
  const busy = state.phase.type === "finding" || state.phase.type === "saving"

  // Changing the address or key forgets the models found for the old one.
  if (event.type === "address.changed" || event.type === "key.changed") {
    if (busy) return { state, effects: NONE }

    const field = event.type === "address.changed" ? { address: event.value } : { key: event.value }

    return { state: { ...state, ...field, phase: { type: "editing" } }, effects: NONE }
  }

  if (event.type === "find") {
    if (busy || !state.address.trim()) return { state, effects: NONE }

    const attempt = state.attempts + 1

    return {
      state: { ...state, phase: { type: "finding", attempt }, attempts: attempt },
      effects: [{ type: "find", attempt, address: state.address, key: key(state) }],
    }
  }

  if (event.type === "found") {
    if (state.phase.type !== "finding" || state.phase.attempt !== event.attempt) return { state, effects: NONE }

    const result = event.result
    const first = result.models[0]

    if (!result.ok || !first)
      return { state: { ...state, phase: { type: "editing", failure: result.reason ?? "no-models" } }, effects: NONE }

    return {
      state: { ...state, phase: { type: "found", address: result.address, models: result.models, selected: first.id } },
      effects: NONE,
    }
  }

  if (event.type === "model.selected") {
    if (state.phase.type !== "found") return { state, effects: NONE }

    return { state: { ...state, phase: { ...state.phase, selected: event.model, failure: undefined } }, effects: NONE }
  }

  if (event.type === "save") {
    if (state.phase.type !== "found") return { state, effects: NONE }

    const attempt = state.attempts + 1
    const found = state.phase
    const model = found.models.find((item) => item.id === found.selected)

    return {
      state: {
        ...state,
        attempts: attempt,
        phase: { type: "saving", attempt, address: found.address, models: found.models, selected: found.selected },
      },
      effects: [
        {
          type: "save",
          attempt,
          address: found.address,
          key: key(state),
          model: found.selected,
          context: model?.context ?? null,
        },
      ],
    }
  }

  if (event.type === "saved") {
    if (state.phase.type !== "saving" || state.phase.attempt !== event.attempt) return { state, effects: NONE }

    if (!event.result.ok)
      return { state: { ...state, phase: back(state.phase, event.result.reason ?? "request") }, effects: NONE }

    return {
      state: { ...state, phase: { type: "saved", model: state.phase.selected } },
      effects: [{ type: "use", model: state.phase.selected }],
    }
  }

  if (state.phase.type === "finding" && state.phase.attempt === event.attempt)
    return { state: { ...state, phase: { type: "editing", failure: "request" } }, effects: NONE }

  if (state.phase.type === "saving" && state.phase.attempt === event.attempt)
    return { state: { ...state, phase: back(state.phase, "request") }, effects: NONE }

  return { state, effects: NONE }
}

function back(phase: LocalModelPhase & { type: "saving" }, failure: LocalModelFailure): LocalModelPhase {
  return { type: "found", address: phase.address, models: phase.models, selected: phase.selected, failure }
}

function key(state: LocalModelState) {
  return state.key.trim() || undefined
}

/** What the form shows. */
export function selectLocalModelView(state: LocalModelState) {
  const phase = state.phase

  return {
    finding: phase.type === "finding",
    saving: phase.type === "saving",
    canFind: !!state.address.trim() && phase.type !== "finding" && phase.type !== "saving",
    models: phase.type === "found" || phase.type === "saving" ? phase.models : [],
    selected: phase.type === "found" || phase.type === "saving" ? phase.selected : undefined,
    canSave: phase.type === "found",
    /** The chosen model's window is small: warn that game changes may fail. */
    small:
      (phase.type === "found" || phase.type === "saving") &&
      isSmallContext(phase.models.find((model) => model.id === phase.selected)?.context ?? null),
    failure: phase.type === "editing" || phase.type === "found" ? phase.failure : undefined,
    saved: phase.type === "saved" ? phase.model : undefined,
  }
}
