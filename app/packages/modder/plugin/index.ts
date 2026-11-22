import { Plugin } from "@spectuner/plugin"
import { SpecTunerAgents } from "./agents.ts"
import { SpecTunerChanges } from "./changes.ts"
import { SpecTunerDisplay } from "./display.ts"
import { SpecTunerLocalModel } from "./local-model.ts"
import { SpecTunerRpc } from "./rpc.ts"
import { SpecTunerTools } from "./tools.ts"

// Shipped as an app resource and loaded by absolute path, so this directory may import only node builtins,
// `effect`, `@spectuner/plugin` (both resolved to the host's copies) and its own files.
export default Plugin.define({
  id: "spectuner.modder",
  setup: async (ctx) => {
    // One plugin instance per Location: the session's folder is the game, and the model never names a path.
    const game = ctx.location.directory
    const exe = () => process.env.SPECTUNER_SIDECAR

    // The modder is the only agent a session can use, also after a config file adds one (see agents.ts).
    const unlock = await SpecTunerAgents.install(ctx)

    // The Changes panel reads the game and its changes, undoes them and puts past ones back, without the model.
    const rpc = await ctx.rpc.register(SpecTunerRpc.Definition, {
      status: (_input, context) => SpecTunerChanges.status({ game, exe, signal: context.signal }),
      undo: (input, context) =>
        SpecTunerChanges.undo({ game, exe, signal: context.signal }, input.changeId).finally(changed),
      restore: (_input, context) => SpecTunerChanges.restore({ game, exe, signal: context.signal }).finally(changed),
      reapply: (input, context) =>
        SpecTunerChanges.reapply({ game, exe, signal: context.signal }, input.changeId).finally(changed),
    })

    // "Use a local model" (see local-model.ts).
    await SpecTunerLocalModel.register(ctx.rpc)

    // Open panels refresh on this; a failed publish only delays the panel until its next refresh.
    function changed() {
      void rpc.events.emit("changed", { directory: game }).catch(() => {})
    }

    await ctx.tool.transform((editor) => {
      // Each call reads as plain words in the conversation (see display.ts).
      SpecTunerTools.make(game, exe, changed)
        .map(SpecTunerDisplay.wrap)
        .forEach((tool) => editor.add(tool))
    })

    return unlock
  },
})
