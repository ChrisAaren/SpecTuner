import type { FileSystem } from "@spectuner/core/filesystem"
import path from "path"
import { describe, expect } from "bun:test"
import { Duration, Effect, Layer, LayerMap } from "effect"
import { Database } from "@spectuner/core/database/database"
import { AppNodeBuilder } from "@spectuner/core/effect/app-node-builder"
import { LayerNode } from "@spectuner/util/effect/layer-node"
import { makeGlobalNode } from "@spectuner/util/effect/app-node"
import { Bus } from "@spectuner/core/bus"
import { Config } from "@spectuner/core/config"
import { Image } from "@spectuner/core/image"
import { Location } from "@spectuner/core/location"
import { LocationServiceMap } from "@spectuner/core/location-service-map"
import type { LocationServices } from "@spectuner/core/location-services"
import { Project } from "@spectuner/core/project"
import { Plugin } from "@spectuner/core/plugin"
import { PluginHooks } from "@spectuner/core/plugin/hooks"
import { AbsolutePath } from "@spectuner/core/schema"
import { Session } from "@spectuner/core/session"
import { SessionExecution } from "@spectuner/core/session/execution"
import { SessionEvent } from "@spectuner/core/session/event"
import { SessionMessage } from "@spectuner/core/session/message"
import { SessionProjector } from "@spectuner/core/session/projector"
import { SessionStore } from "@spectuner/core/session/store"
import { SessionInbox } from "@spectuner/core/session/inbox"
import { Skill } from "@spectuner/core/skill"
import { Event } from "@spectuner/schema/event"
import { testEffect } from "./lib/effect"
import { globalProjectNode } from "./lib/project"
import { organizationPolicy, registerIntegrationPolicy } from "./fixture/policy"

const location = Location.Ref.make({ directory: AbsolutePath.make("/project") })
// The organization behind this location's Console connection denies the skill.
const blocked = Location.Ref.make({ directory: AbsolutePath.make("/blocked") })
const info = Skill.Info.make({
  id: Skill.ID.make("effect"),
  name: Skill.Name.make("Effect"),
  description: "Effect guidance",
  path: AbsolutePath.make(path.resolve("/skills/effect.md")),
  content: "  Use Effect\n",
})
const locations = makeGlobalNode({
  service: LocationServiceMap.Service,
  layer: Layer.effect(
    LocationServiceMap.Service,
    LayerMap.make(
      (ref: Location.Ref) =>
        // These tests need skill activation and prompt preparation from the same location services.
        // oxlint-disable-next-line typescript-eslint/no-unsafe-type-assertion
        Layer.mergeAll(
          Layer.effectDiscard(
            Effect.gen(function* () {
              const skills = yield* Skill.Service
              yield* skills.transform((editor) => editor.add(info))
              yield* registerIntegrationPolicy({ skill: skills })
            }),
          ).pipe(
            Layer.provideMerge(
              LayerNode.compile(
                LayerNode.group([PluginHooks.node, Image.node, Skill.node, Config.node]),
                {
                  replacements: [
                    Config.node.replace(
                      Config.testLayer(
                        ref.directory === blocked.directory
                          ? [
                              organizationPolicy([
                                { action: "skill.use", resource: "effect", effect: "deny" },
                              ]),
                            ]
                          : [],
                      ),
                    ),
                  ],
                },
              ),
            ),
          ),
          Layer.mock(Plugin.Service, { awaitActivation: Effect.void }),
        ) as unknown as Layer.Layer<LocationServices, FileSystem.DirectoryNotFoundError>,
      { idleTimeToLive: Duration.infinity },
    ),
  ),
  deps: [],
})
const it = testEffect(
  AppNodeBuilder.build(
    LayerNode.group([
      Database.node,
      Bus.node,
      SessionProjector.node,
      SessionStore.node,
      Session.node,
      LocationServiceMap.node,
    ]),
    [
      LocationServiceMap.node.replace(locations),
      Project.node.replace(globalProjectNode),
      SessionExecution.node.replace(SessionExecution.noopLayer),
    ],
  ),
)

describe("Session.skill", () => {
  it.effect("refuses blocked skill mentions and standalone activation without writing their content", () =>
    Effect.gen(function* () {
      const sessions = yield* Session.Service
      const session = yield* sessions.create({ location: blocked })
      expect(
        yield* sessions.skill({ sessionID: session.id, skill: info.id, resume: false }).pipe(Effect.flip),
      ).toMatchObject({ _tag: "Session.SkillNotFoundError", skill: info.id })
      expect(
        yield* sessions
          .prompt({
            sessionID: session.id,
            text: "@effect",
            skills: [{ id: info.id, mention: { start: 0, end: 7, text: "@effect" } }],
            resume: false,
          })
          .pipe(Effect.flip),
      ).toMatchObject({ _tag: "Session.SkillNotFoundError", skill: info.id })
      expect(yield* sessions.messages({ sessionID: session.id })).toEqual([])
      expect(yield* sessions.inbox(session.id)).toEqual([])
    }),
  )

  it.effect("materializes mentioned skills on their owning prompt", () =>
    Effect.gen(function* () {
      const sessions = yield* Session.Service
      const database = yield* Database.Service
      const bus = yield* Bus.Service
      const session = yield* sessions.create({ location })
      const id = SessionMessage.ID.make("msg_skill_attachment")

      yield* sessions.prompt({
        id,
        sessionID: session.id,
        text: "Apply @effect and @effect",
        skills: [
          { id: Skill.ID.make("effect"), mention: { start: 6, end: 13, text: "@effect" } },
          { id: Skill.ID.make("effect"), mention: { start: 18, end: 25, text: "@effect" } },
        ],
        resume: false,
      })
      expect(yield* sessions.messages({ sessionID: session.id })).toEqual([])
      yield* SessionInbox.promote(database.db, bus, session.id, "steer")

      expect(yield* sessions.messages({ sessionID: session.id })).toEqual([
        expect.objectContaining({
          id,
          type: "user",
          text: "Apply @effect and @effect",
          skills: [
            {
              id: "effect",
              name: "Effect",
              text: Skill.toModelOutput(info, []),
              mention: { start: 6, end: 13, text: "@effect" },
            },
            {
              id: "effect",
              name: "Effect",
              mention: { start: 18, end: 25, text: "@effect" },
            },
          ],
        }),
      ])
    }),
  )

  it.effect("excludes mentioned skills when forking before their prompt", () =>
    Effect.gen(function* () {
      const sessions = yield* Session.Service
      const database = yield* Database.Service
      const bus = yield* Bus.Service
      const session = yield* sessions.create({ location })
      const initial = SessionMessage.ID.make("msg_before_skill_attachment")
      const selected = SessionMessage.ID.make("msg_fork_skill_attachment")

      yield* sessions.prompt({ id: initial, sessionID: session.id, text: "Before the skill", resume: false })
      yield* SessionInbox.promote(database.db, bus, session.id, "steer")
      yield* sessions.prompt({
        id: selected,
        sessionID: session.id,
        text: "Apply @effect",
        skills: [{ id: info.id, mention: { start: 6, end: 13, text: "@effect" } }],
        resume: false,
      })
      yield* SessionInbox.promote(database.db, bus, session.id, "steer")
      const forked = yield* sessions.fork({ sessionID: session.id, before: selected })

      expect(yield* sessions.messages({ sessionID: forked.id })).toEqual([
        expect.objectContaining({ type: "user", text: "Before the skill" }),
      ])
    }),
  )

  it.effect("publishes raw standalone content under the caller-supplied ID without inbox admission", () =>
    Effect.gen(function* () {
      const sessions = yield* Session.Service
      const bus = yield* Bus.Service
      const session = yield* sessions.create({ location })
      const id = SessionMessage.ID.make("msg_caller_skill")
      const locations = yield* LocationServiceMap.Service
      yield* Skill.Service.use((skills) => skills.list()).pipe(Effect.provide(locations.get(session.location)))
      const events: Event.Payload[] = []
      yield* bus.listen((event) =>
        Effect.sync(() => {
          events.push(event)
        }),
      )

      yield* sessions.skill({ messageID: id, sessionID: session.id, skill: Skill.ID.make("effect"), resume: false })

      expect(events).toEqual([
        expect.objectContaining({
          id: "evt_caller_skill",
          type: SessionEvent.Skill.Activated.type,
          data: { sessionID: session.id, id: info.id, name: info.name, text: info.content },
        }),
      ])
      expect(yield* sessions.messages({ sessionID: session.id })).toEqual([
        expect.objectContaining({ id, type: "skill", skill: "effect", name: "Effect", text: info.content }),
      ])
      expect(yield* sessions.inbox(session.id)).toEqual([])
    }),
  )
})
