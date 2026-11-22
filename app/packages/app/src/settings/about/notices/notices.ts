import opencodeLicense from "./opencode-mit.txt?raw"

/**
 * Third-party software that ships inside the app, with the attribution its license asks redistributions to carry.
 * Notices and license texts are reproduced verbatim from each project, so they are not translated.
 */
export const notices = [
  {
    name: "opencode",
    detailKey: "settings.about.notices.opencode" as const,
    license: "MIT License",
    notice: "Copyright (c) 2025 opencode",
    text: opencodeLicense,
  },
]
