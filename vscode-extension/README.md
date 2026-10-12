# Macro Deck Bridge

Companion extension for the **VS Code Plugin** for Macro Deck 3. See the repository README for setup.

1. Install this extension.
2. Run **Macro Deck: Copy Pairing Token** from the Command Palette.
3. In Macro Deck, set up the VS Code Plugin and paste the token.

The extension connects only to `127.0.0.1` on the configured port (default 51741). It sends the active file name (never its path), language, workspace name, Git branch, problem counts and debug state to Macro Deck, and runs the commands and tasks your deck asks for.
