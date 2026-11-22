import { compose } from "@spectuner/plugin/gui/host"
import spectunerChanges from "./spectuner-changes"

/** Built-in plugins with their main entries, by id. Lists every built-in so their ids stay reserved. */
// prettier-ignore
export const builtins = compose({
  "spectuner-changes": spectunerChanges,
})
