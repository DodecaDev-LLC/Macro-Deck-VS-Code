# Changelog

## 1.0.0

First release, together with version 1.0.0 of the VS Code Plugin for Macro Deck.

- Pair with Macro Deck through a token copied from the Command Palette; every VS Code window on the
  computer connects with it.
- Macro Deck buttons can run any VS Code command (with arguments) and any workspace task.
- Reports the active file, language, workspace, Git branch, error and warning counts, unsaved files and
  debugging state to Macro Deck.
- Forwards file saves, debug sessions starting and stopping, and finished tasks as Macro Deck events.
- Choose which window plain buttons control: pin a window from the status bar or the Command Palette.
  The status bar shows the pinned window, the default window, and the windows that are neither.
- Works in WSL, SSH and container windows.
