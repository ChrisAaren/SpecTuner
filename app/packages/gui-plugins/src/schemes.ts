import type { CustomScheme } from "electron"

// URL schemes the built-ins register before app.ready. Only the owning plugin's partition installs a handler, the
// scheme is not local, cannot bypass CSP, and does not support service workers. No SpecTuner built-in needs one.
export const schemes: CustomScheme[] = []
