# VS Code Plugin for Macro Deck

Control Visual Studio Code from Macro Deck 3: run any editor command or workspace task from a button,
and show the active file, Git branch, problem counts and debug state on your deck.

## How it works

VS Code has no API that another process can call, so the integration has two halves:

```
Macro Deck ──protocol──▶ DodecaDev.VsCode (macro-deck-plugin/)
                           └─ BridgeServer: WebSocket on 127.0.0.1:51741/macro-deck-bridge
                                 ▲            ▲
                 Macro Deck Bridge extension, one per VS Code window (vscode-extension/)
```

The repository holds both, side by side:

| Folder | What | Built with |
| --- | --- | --- |
| [`macro-deck-plugin/`](macro-deck-plugin/) | The Macro Deck plugin (.NET 10): solution, `src/`, `tests/` | `dotnet`, `macrodeck-plugin` |
| [`vscode-extension/`](vscode-extension/) | The Macro Deck Bridge extension (TypeScript) | `npm`, `vsce` |
| `.github/workflows/` | `release.yml` publishes the plugin to the Macro Deck Store; `vscode-extension.yml` builds the .vsix and attaches it to the same release | GitHub Actions |

- **`macro-deck-plugin/src/DodecaDev.VsCode`**: the Macro Deck plugin. It hosts a small loopback WebSocket hub and sends
  actions to the window each button targets (see *Which window a button controls*).
- **`vscode-extension/`**: the companion VS Code extension. Every window connects to the hub, pushes
  its state (debounced), forwards events, and runs the commands and tasks it is asked to.
  `extensionKind: ["ui"]` keeps it on the machine Macro Deck runs on, even in WSL, SSH or container
  windows.

**Pairing.** The extension generates a token (shared by all windows of a VS Code profile, kept in VS Code's
secret storage). The user copies it with *Macro Deck: Copy Pairing Token* and pastes it into the plugin's
setup. Neither side ever sends the token: each proves it holds it with an HMAC over the other's nonce
(`macro-deck-plugin/src/DodecaDev.VsCode/Bridge/BridgeAuth.cs`, `vscode-extension/src/auth.ts`), the plugin first, so a window never obeys a
process that merely took the port. Connections with an `Origin` header (browsers) are refused.

### What it offers

| Kind | Id | Notes |
| --- | --- | --- |
| Action | `run-command` | Any command id, with optional JSON arguments; dropdown lists the window's commands |
| Action | `run-task` | Any task from the workspace (tasks.json, npm scripts, ...) |
| Action | `select-window` | Pin or unpin the current window, switch to the next one, or pin a workspace. Its Macro Deck button shows *Pinned* or *Following*; in VS Code, the pinned window's status bar item shows a pin |
| Variables | `vscode_workspace`, `vscode_active_file`, `vscode_language`, `vscode_git_branch`, `vscode_errors`, `vscode_warnings`, `vscode_unsaved_files`, `vscode_debugging` | About the default window, the one buttons set to *Active window* control |
| Variables | `vscode_connected`, `vscode_window_count`, `vscode_target_pinned`, `vscode_bridge_status` | About the bridge: whether a window is connected, how many, whether one is pinned, and whether the plugin is listening |
| Events | `file-saved`, `debug-started`, `debug-stopped`, `task-finished` | From any window |
| Issue | `port-in-use` | Opens setup to choose another port |

### Which window a button controls

Every action has an optional **Window** field. In order:

1. **A workspace chosen on the button** (picked from the open windows, or typed by name): that project,
   wherever you are working. **All windows** sends to every window.
2. **The pinned window**, when one is pinned: with *Macro Deck: Control This Window*, a click on the
   status bar item, or the *Select VS Code window* action. Pins remember the workspace, so they survive
   a window reload, and stay dormant while that workspace is closed.
3. **The focused window**, else the one used most recently.

The status bar marks the *default* window, the one buttons set to *Active window* control: a pin icon
when it is pinned, a plain plug when it follows focus, and a dimmed plug in every other window (whose
tooltip names the default). Buttons bound to a workspace or to All windows ignore the default.

### Building and testing both halves

From the repository root:

```bash
(cd macro-deck-plugin && dotnet build && dotnet test)                       # plugin
(cd vscode-extension && npm install && npm test)                            # extension (Node)
(cd macro-deck-plugin && dotnet test --filter TestCategory=Interop)         # real TS client against the real C# server
(cd vscode-extension && npx vsce package --out ../artifacts/)               # .vsix to install in VS Code
(cd macro-deck-plugin/src/DodecaDev.VsCode && macrodeck-plugin build --output ../../../artifacts)   # .macroDeckPlugin
```

The interop test needs `npm test` to have run once, so `vscode-extension/out/` exists.

The two halves share a wire protocol (`ProtocolVersion` / `PROTOCOL_VERSION`); change both together, and
keep the HMAC test vector in `BridgeAuthTests` and `vscode-extension/test/bridge.test.js` in step.

## Releasing

Publishing a GitHub release (tag `v1.2.3`) does both halves at once: the plugin goes to the Creator
Portal for review, and the matching `macro-deck-bridge-1.2.3.vsix` is attached to the release.
The same release publishes that `.vsix` to the VS Code Marketplace under the `dodecadev` publisher.

Marketplace publishing signs in to Azure as the managed identity `id-vsce-publisher` with GitHub's OIDC
token: no secret is stored. Its federated credential trusts only jobs in this repository's `marketplace`
environment, which only `main` and `v*` tags may use. The identity's IDs are the repository variables
`AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`. To publish an existing release again,
run the *VS Code extension* workflow by hand with `publish_tag` set to its tag.

## Icon

`macro-deck-plugin/src/DodecaDev.VsCode/Assets/icon.svg` is the source. `vscode-extension/images/icon.png` is
a 256 px render of it, because the VS Code Marketplace rejects SVG icons: re-render it when the SVG
changes. The icon is a macro pad whose keys are what the plugin does, deliberately without the VS Code or
Macro Deck logos, which are their owners' trademarks.

## License

MIT, see [LICENSE](LICENSE).


