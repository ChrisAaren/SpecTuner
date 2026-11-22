import { useWorkspaceLocation } from "@/workspaces/location"
import { SpecTunerProfile } from "./profile"

// SpecTuner. The new chat's heading in place of OpenCode's wordmark (and the games hidden behind it):
// the product name and the game folder the chat will work on, since the project and branch row is not shown.

export function SpecTunerDraftHeading() {
  const location = useWorkspaceLocation()

  return (
    <div data-component="spectuner-draft-heading" class="flex flex-col items-center gap-2 text-center">
      <h1 class="text-[32px] leading-10 tracking-[-0.5px] text-v2-text-text-base [font-weight:600]">
        {SpecTunerProfile.name}
      </h1>
      <p
        dir="ltr"
        class="max-w-full truncate text-[13px] leading-5 tracking-[-0.04px] text-v2-text-text-muted [font-weight:440]"
      >
        {location().directory}
      </p>
    </div>
  )
}
