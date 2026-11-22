import type { SpecTunerEvent } from "@spectuner/client"
import type { SelectedEvent, SubscribeOptions } from "@spectuner/client/promise/api"

type EventType = Exclude<SpecTunerEvent["type"], "server.connected">

export interface EventDomain {
  readonly subscribe: <Type extends EventType = EventType>(
    options?: SubscribeOptions<Type>,
  ) => AsyncIterable<SelectedEvent<SpecTunerEvent, Type>>
}
