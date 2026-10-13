# Macro Deck Bridge

Control VS Code from your [Macro Deck](https://macro-deck.app) buttons. This extension connects VS Code to
the **VS Code Plugin** in Macro Deck 3. Together they let a button:

- **run any VS Code command**, including commands from other extensions, with optional arguments
- **run a task** from your workspace: a build, a test run, an npm script
- **show what VS Code is doing**: the active file, language, workspace, Git branch, error and warning
  counts, unsaved files, and whether you are debugging

Macro Deck can also react to VS Code: when a file is saved, a debug session starts or stops, or a task
finishes.

## Requirements

- **Macro Deck 3**, with the **VS Code Plugin** installed from the Macro Deck Store
- **VS Code 1.95** or later, on the same computer as Macro Deck

## Set it up

1. Install this extension.
2. Run **Macro Deck: Copy Pairing Token** from the Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`).
3. In Macro Deck, open the VS Code Plugin's setup and paste the token.

Within a few seconds the **Macro Deck** item in the status bar shows a plug: VS Code is connected. Every
VS Code window on the computer connects on its own; you only pair once.

## Several windows

A button controls the window you used last, unless you choose otherwise:

- **Pin a window** to keep Macro Deck on it while you work elsewhere: click the Macro Deck item in its
  status bar, or run **Macro Deck: Control This Window**. Pins remember the project, so they survive a
  window reload.
- **Bind a button to a project** with the button's *Window* setting in Macro Deck, or send it to *All
  windows*.

The status bar shows which window plain buttons control:

| Status bar | Meaning |
| --- | --- |
| Pin icon | This window is pinned: buttons set to *Active window* control it |
| Plug | This window is the default, because you used it last |
| Dimmed plug | Connected, but another window is the default |
| Unplugged icon | Waiting for Macro Deck: is it running, with the VS Code Plugin set up? |
| Warning icon | Macro Deck rejected the pairing token: copy it again and paste it into the plugin's setup |

## Commands

| Command | What it does |
| --- | --- |
| Macro Deck: Copy Pairing Token | Copies this computer's token for the plugin's setup |
| Macro Deck: Reset Pairing Token | Makes a new token; Macro Deck stops accepting VS Code until you paste the new one |
| Macro Deck: Control This Window | Pins this window |
| Macro Deck: Unpin Window and Follow the Focused One | Goes back to controlling the window you used last |
| Macro Deck: Reconnect | Reconnects now instead of waiting for the next retry |

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `macroDeckBridge.enabled` | `true` | Connect this VS Code to Macro Deck |
| `macroDeckBridge.port` | `51741` | The port of the VS Code Plugin; must match the port in the plugin's setup |

## Remote windows (WSL, SSH, containers)

The extension runs on your own computer even when a window is connected to WSL, SSH or a container, so it
can reach Macro Deck there. The Git branch is read from the repository's files, so it works in remote
windows too.

## Privacy and security

- **Local only.** The extension talks only to Macro Deck on `127.0.0.1`. Nothing leaves your computer.
- **Paired.** Both sides prove they hold the pairing token without ever sending it, and connections from
  web browsers are refused, so neither a web page nor an unpaired program can drive VS Code through it.
- **What Macro Deck receives:** the active file's name (not its path), its language, the workspace name
  and folder location (to recognise the project across reloads), the Git branch, problem counts, the
  number of unsaved files, whether you are debugging, and which window has focus.
- **What Macro Deck can do:** run the commands and tasks your buttons are set up to run, in the windows
  they target. The token is kept in VS Code's secret storage.

## Links

- [Source code and issues](https://github.com/DodecaDev-LLC/Macro-Deck-VS-Code)
- [Changelog](CHANGELOG.md)

Macro Deck Bridge is made by DodecaDev. It is not affiliated with or endorsed by Microsoft or the Macro
Deck project.
