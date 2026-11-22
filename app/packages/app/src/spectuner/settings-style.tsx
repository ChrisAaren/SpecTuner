import { SpecTunerProfile } from "./profile"

// SpecTuner. Hides the settings rows SpecTuner leaves out on the pages it keeps. Each upstream row
// marks its control with `data-action`, so one stylesheet hides the row around it without editing the pages; the
// search index drops the same rows (`SpecTunerProfile.settingsRow`).

const css = SpecTunerProfile.hiddenSettingsRows
  .flatMap((target) => [`[data-component="settings-row"]:has([data-action="${target}"])`, `[data-action="${target}"]`])
  .join(",\n")
  .concat(" {\n  display: none !important;\n}\n")

export function SpecTunerSettingsStyle() {
  return <style data-spectuner="settings-rows">{css}</style>
}
