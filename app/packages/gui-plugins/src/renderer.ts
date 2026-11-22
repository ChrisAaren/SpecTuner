import { compose, type Entry } from "@spectuner/plugin/gui/host"
import spectunerChanges from "./spectuner-changes"
import spectunerChangesRenderer from "./spectuner-changes/renderer"

// The window renders once every built-in is active, so the small renderer entries load with the app. Heavy UI stays
// behind `lazy()` inside them.
const eager = (setup: Awaited<ReturnType<NonNullable<Entry["windowEntry"]>>>["default"]) => () =>
  Promise.resolve({ default: setup })

/** Built-in plugins with their window entries, by id. The only place host builds name built-ins. */
// prettier-ignore
export const builtins = compose({
  "spectuner-changes": { ...spectunerChanges, windowEntry: eager(spectunerChangesRenderer) },
})
