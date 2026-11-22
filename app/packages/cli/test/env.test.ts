import { expect, test } from "bun:test"
import { Env } from "../src/env"

test("session environment omits server credentials", () => {
  const previousPassword = process.env.SPECTUNER_PASSWORD
  const previousLegacyPassword = process.env.SPECTUNER_SERVER_PASSWORD
  const previousValue = process.env.SPECTUNER_SESSION_ENV_TEST
  process.env.SPECTUNER_PASSWORD = "password"
  process.env.SPECTUNER_SERVER_PASSWORD = "legacy"
  process.env.SPECTUNER_SESSION_ENV_TEST = "included"

  const environment = Env.session()

  if (previousPassword === undefined) delete process.env.SPECTUNER_PASSWORD
  else process.env.SPECTUNER_PASSWORD = previousPassword
  if (previousLegacyPassword === undefined) delete process.env.SPECTUNER_SERVER_PASSWORD
  else process.env.SPECTUNER_SERVER_PASSWORD = previousLegacyPassword
  if (previousValue === undefined) delete process.env.SPECTUNER_SESSION_ENV_TEST
  else process.env.SPECTUNER_SESSION_ENV_TEST = previousValue

  expect(environment.SPECTUNER_PASSWORD).toBeUndefined()
  expect(environment.SPECTUNER_SERVER_PASSWORD).toBeUndefined()
  expect(environment.SPECTUNER_SESSION_ENV_TEST).toBe("included")
})
