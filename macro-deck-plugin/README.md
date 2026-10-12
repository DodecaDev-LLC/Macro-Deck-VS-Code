# VS Code Plugin: the Macro Deck plugin

The .NET half of the [VS Code Plugin for Macro Deck](../README.md). Macro Deck runs it, and it hosts the
WebSocket bridge that the [Macro Deck Bridge extension](../vscode-extension/) in each VS Code window
connects to. [AGENTS.md](AGENTS.md) holds the rules for changing this code. Run every command below from
this folder (`macro-deck-plugin/`) unless it says otherwise.

## What's in here

```
DodecaDev.VsCode.slnx
src/DodecaDev.VsCode/
  Program.cs                  host setup: the integration, localization, the BridgeServer singleton
  PluginIntegration.cs        variables, events, setup flow and issues, all reading the bridge
  Actions/                    run-command, run-task, select-window, and the shared Window field
  Bridge/                     BridgeServer (loopback WebSocket hub), VsCodeWindow, pairing proof
  ConfigFlow/                 the pairing setup: token and port
  Localization/Strings.resx   every user-facing string
  manifest.json               identity, Store metadata, one entrypoint per platform
tests/DodecaDev.VsCode.Tests/ NUnit + PluginTestHarness; FakeVsCodeWindow plays the extension
```

## Requirements

- **.NET 10 SDK.**
- **The `macrodeck-plugin` CLI, at or above the Store's minimum** (currently `3.0.0-beta.16`). Install it
  by exact version, because a plain `--prerelease` install can pick an older build:
  `dotnet tool install --global MacroDeck.Plugin.Cli --version 3.0.0-beta.16`
- **Macro Deck 3 with Developer Mode on**, to run the plugin against the real app.
- **Node.js 22** if you run the interop test, which drives the real extension code.

The Macro Deck packages float within the beta line (`3.0.0-beta.*` in `Directory.Packages.props`).

## Build and test

```bash
dotnet build                                          # warning-free; the analyzers catch contract mistakes
dotnet test                                           # 25 tests; the bridge runs on real loopback sockets
dotnet test --filter TestCategory=Interop             # the extension's TypeScript client against this bridge
macrodeck-plugin test --project src/DodecaDev.VsCode  # conformance suite; every Required check must pass
```

The interop test needs the extension's test build, so run `npm install && npm test` in
`../vscode-extension` once first.

## Run it against Macro Deck

```bash
macrodeck-plugin run --project src/DodecaDev.VsCode
```

1. Approve the pairing prompt in Macro Deck. The plugin then shows up under Integrations.
2. In VS Code, with the Macro Deck Bridge extension installed, run **Macro Deck: Copy Pairing Token**.
3. Open the plugin's setup in Macro Deck and paste the token. Leave the port at 51741 unless something else
   uses it.
4. Within about 30 seconds, VS Code's status bar shows the Macro Deck plug. `vscode_bridge_status` should
   read `listening`.

Restart the plugin after changing its actions, variables or events, because Macro Deck reads them only at
startup. The **Macro Deck - Real Host** launch profile does the same as `macrodeck-plugin run`, under your
IDE's debugger.

**Working in WSL with Macro Deck on Windows:** run the plugin from Windows, not from WSL, so it listens on
the same loopback as Macro Deck and the VS Code extension. From PowerShell, in the repository opened over
`\\wsl.localhost\...`:

```powershell
macrodeck-plugin run --project .\macro-deck-plugin\src\DodecaDev.VsCode
```

## Package

From `src/DodecaDev.VsCode/`, so the package lands in the repository's shared `artifacts/` folder, next to
the `.vsix`:

```bash
macrodeck-plugin build --output ../../../artifacts
macrodeck-plugin validate --level publication --artifact ../../../artifacts/com.dodecadev.vscode-1.0.0.macroDeckPlugin
```

`build` compiles every platform in `manifest.json` (win-x64, osx-arm64, linux-x64) and packs them into one
`.macroDeckPlugin`. Don't zip a `dotnet publish` output by hand.

`macrodeck-plugin test --artifact` can't launch the packed plugin with Ubuntu's own .NET build, which
reports the machine as `ubuntu.24.04-x64` instead of `linux-x64`. The release workflow runs that check on
GitHub's runners, and `test --project` covers the same code locally.

## Release

Publish a GitHub release with a tag like `v1.2.3`. `.github/workflows/release.yml` builds, tests and runs
conformance, then uploads the plugin to the Creator Portal for review; the tag sets the version. The same
release attaches the matching `.vsix` (see `vscode-extension.yml`). The workflow pins the CLI with
`cli-version`: keep it at or above `minimumCliVersion` from
https://api.macro-deck.app/api/v1/public/dependency-policy/sdk.

## Changing the code

- **Strings:** add every user-facing string to `Localization/Strings.resx` first, then use the generated
  `Strings.*` member. Log messages stay English literals.
- **Wire protocol:** a change to the protocol between the bridge and the extension lands in both halves. Keep
  the HMAC test vector in `BridgeAuthTests` and `../vscode-extension/test/bridge.test.js` in step.
- **Order of declarations:** declare a member before the members that use it, so the file reads top-down:
  nested types and constants, fields, properties, the constructor, then each helper above its callers.
- **Persisted ids:** action, variable, event and state ids are stored in users' profiles. Never rename one
  that has shipped.

### Building against an unreleased SDK

Pack the SDK from a Macro Deck 3 checkout into `local-feed/` (already a package source in
`NuGet.config`), then build against that version:

```bash
dotnet pack MacroDeck.slnx -c Release -p:Version=3.0.0-local.1 -o <this folder>/local-feed   # in the Macro Deck checkout
dotnet build -p:MacroDeckSdkVersion=3.0.0-local.1
```

Use a `3.0.0-local.N` version, so a hand-built package never takes the name of a published one.
