import { expect, story } from "../../stories/playwright/story"

story("the connect screen can retry a failed health check after scanning a one-time QR code", async ({ mount }) => {
  const component = await mount("app-connect-to-server--retry-after-scan")
  await component.getByRole("button", { name: "Read fixture QR code" }).click()
  await expect(component.getByRole("alert")).toHaveText(
    "Could not connect. Check the server address and password, then try again.",
  )
  await expect(component.getByLabel("Pairing link", { exact: true })).toHaveValue(
    "https://build.example.test/auth/connect/example",
  )

  await component.getByRole("button", { name: "Connect", exact: true }).click()
  await expect(component.getByRole("status")).toHaveText("Connected to https://build.example.test in this preview.")
})
