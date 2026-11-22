import { createReactiveTimelineProjection } from "@spectuner/session-ui/timeline/projection"

export { reuseTimelineRows } from "@spectuner/session-ui/timeline/projection"

export function createTimelineProjection(input: Parameters<typeof createReactiveTimelineProjection>[0]) {
  return createReactiveTimelineProjection(input)
}
