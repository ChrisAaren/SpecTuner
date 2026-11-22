import type { ElectronNative } from "../preload/types"

declare global {
  interface ImportMetaEnv {
    readonly SPECTUNER_TEST_ONBOARDING: boolean
  }

  interface Window {
    electron: ElectronNative
    __SPECTUNER__?: {
      deepLinks?: string[]
    }
  }
}
