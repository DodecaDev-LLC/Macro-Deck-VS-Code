# CLAUDE.md

This repository has two halves:

- `macro-deck-plugin/`: the Macro Deck plugin (.NET). Follow [macro-deck-plugin/AGENTS.md](macro-deck-plugin/AGENTS.md),
  the authoritative guidance for plugin code, verification, packing and workflow conventions. Run `dotnet`
  and `macrodeck-plugin` commands from inside that folder.
- `vscode-extension/`: the Macro Deck Bridge VS Code extension (TypeScript). `npm test` builds and tests it.

The two share a wire protocol: change both together, and keep the HMAC test vector in
`macro-deck-plugin/tests/DodecaDev.VsCode.Tests/BridgeInteropTests.cs` and
`vscode-extension/test/bridge.test.js` in step. See [README.md](README.md) for how they fit together.
