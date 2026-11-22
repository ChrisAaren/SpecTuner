import { expect, test } from "bun:test"
import { SpecTunerProfile } from "./profile"

// The composer's voice call button (voice/composer.tsx) never shows: not on the dev or beta channel, and not when the
// voice experiment is switched on, because SpecTuner has no voice feature and the server refuses its endpoints.
test.each([true, false])("no voice calls when upstream would show them: %p", (upstream) => {
  expect(SpecTunerProfile.voiceCalls(upstream)).toBe(false)
})
