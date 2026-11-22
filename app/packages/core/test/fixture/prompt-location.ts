import type { FileSystem } from "@spectuner/core/filesystem"
import { Bus } from "@spectuner/core/bus"
import { Image } from "@spectuner/core/image"
import { LocationServiceMap } from "@spectuner/core/location-service-map"
import type { LocationServices } from "@spectuner/core/location-services"
import { Plugin } from "@spectuner/core/plugin"
import { PluginHooks } from "@spectuner/core/plugin/hooks"
import { Skill } from "@spectuner/core/skill"
import type { Location } from "@spectuner/schema/location"
import { makeGlobalNode } from "@spectuner/util/effect/app-node"
import { LayerNode } from "@spectuner/util/effect/layer-node"
import { Effect, Layer, LayerMap } from "effect"

// Plain-prompt unit fixtures use virtual directories.
export const promptLocationNode = makeGlobalNode({
  service: LocationServiceMap.Service,
  layer: Layer.effect(
    LocationServiceMap.Service,
    Effect.gen(function* () {
      const bus = yield* Bus.Service
      return yield* LayerMap.make(
        (_ref: Location.Ref) =>
          LayerNode.compile(LayerNode.group([PluginHooks.node, Image.node, Skill.node, Plugin.node]), {
            replacements: [
              Bus.node.replace(Layer.succeed(Bus.Service, bus)),
              Plugin.node.replace(Layer.mock(Plugin.Service, { awaitActivation: Effect.void })),
            ],
          }) as unknown as Layer.Layer<LocationServices, FileSystem.DirectoryNotFoundError>,
      )
    }),
  ),
  deps: [Bus.node],
})
