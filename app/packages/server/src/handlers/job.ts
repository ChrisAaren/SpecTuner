import { Job } from "@spectuner/core/job"
import { Session } from "@spectuner/core/session"
import { SessionBackground } from "@spectuner/core/session/background"
import type { ListQuery } from "@spectuner/protocol/groups/job"
import { Effect, Option } from "effect"
import { missingSession } from "./session-error"

/** `job.*` in the `server.experimental` group. */
export const makeJob = Effect.gen(function* () {
  const jobs = yield* Job.Service
  const sessions = yield* Session.Service
  const lookup = {
    get: (sessionID: Session.ID) => sessions.get(sessionID).pipe(Effect.option, Effect.map(Option.getOrUndefined)),
  }

  return {
    list: Effect.fn("JobHandler.list")(function* (query: ListQuery) {
      if (query.sessionID !== undefined) {
        yield* sessions.get(query.sessionID).pipe(Effect.catchTag("Session.NotFoundError", missingSession))
      }
      return {
        data: yield* SessionBackground.list(jobs, lookup, query),
      }
    }),
    cancel: Effect.fn("JobHandler.cancel")(function* (id: string) {
      yield* jobs.cancel(id)
    }),
  }
})
