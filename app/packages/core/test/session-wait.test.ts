import { describe, expect } from "bun:test"
import { Effect, Layer } from "effect"
import { Database } from "@spectuner/core/database/database"
import { AppNodeBuilder } from "@spectuner/core/effect/app-node-builder"
import { LayerNode } from "@spectuner/util/effect/layer-node"
import { Bus } from "@spectuner/core/bus"
import { Location } from "@spectuner/core/location"
import { Project } from "@spectuner/core/project"
import { AbsolutePath } from "@spectuner/core/schema"
import { Session } from "@spectuner/core/session"
import { SessionProjector } from "@spectuner/core/session/projector"
import { SessionExecution } from "@spectuner/core/session/execution"
import { SessionStore } from "@spectuner/core/session/store"
import { testEffect } from "./lib/effect"
import { globalProjectNode } from "./lib/project"

const location = Location.Ref.make({ directory: AbsolutePath.make("/project") })
const awaited: Session.ID[] = []
const execution = Layer.mock(SessionExecution.Service, {
  awaitIdle: (sessionID) => Effect.sync(() => awaited.push(sessionID)),
})
const it = testEffect(
  AppNodeBuilder.build(
    LayerNode.group([Database.node, Bus.node, SessionProjector.node, SessionStore.node, Session.node]),
    [Project.node.replace(globalProjectNode), SessionExecution.node.replace(execution)],
  ),
)

describe("Session.wait", () => {
  it.effect("delegates to SessionExecution.awaitIdle", () =>
    Effect.gen(function* () {
      awaited.length = 0
      const sessions = yield* Session.Service
      const session = yield* sessions.create({ location })

      yield* sessions.wait(session.id)

      expect(awaited).toEqual([session.id])
    }),
  )
})
