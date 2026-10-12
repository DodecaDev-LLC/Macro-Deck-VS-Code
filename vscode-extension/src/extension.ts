import * as path from "node:path";
import { randomUUID } from "node:crypto";
import * as vscode from "vscode";
import { newPairingToken } from "./auth";
import { BridgeClient, ConnectionStatus } from "./bridgeClient";
import { branchFromHead, gitDirFromDotGitFile } from "./gitHead";

const TOKEN_KEY = "macroDeckBridge.pairingToken";
const STATE_DEBOUNCE_MS = 250;

let client: BridgeClient | undefined;
let statusItem: vscode.StatusBarItem;
let stateTimer: NodeJS.Timeout | undefined;
const windowId = randomUUID();

export async function activate(context: vscode.ExtensionContext): Promise<void> {
	statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 0);
	statusItem.name = "Macro Deck";
	context.subscriptions.push(statusItem);

	context.subscriptions.push(
		vscode.commands.registerCommand("macroDeckBridge.copyPairingToken", async () => {
			await vscode.env.clipboard.writeText(await pairingToken(context));
			void vscode.window.showInformationMessage(
				"Pairing token copied. In Macro Deck, set up the VS Code Plugin and paste it into \"Pairing token\".");
		}),
		vscode.commands.registerCommand("macroDeckBridge.resetPairingToken", async () => {
			const answer = await vscode.window.showWarningMessage(
				"Reset the pairing token? Macro Deck will stop accepting VS Code until you paste the new token into the plugin's setup.",
				{ modal: true }, "Reset");
			if (answer === "Reset") {
				await context.secrets.store(TOKEN_KEY, newPairingToken());
				await vscode.commands.executeCommand("macroDeckBridge.copyPairingToken");
			}
		}),
		vscode.commands.registerCommand("macroDeckBridge.reconnect", () => restart(context)),
		// Every window shares the token, so a reset in one window reconnects all of them.
		context.secrets.onDidChange((e) => e.key === TOKEN_KEY ? restart(context) : undefined),
		vscode.workspace.onDidChangeConfiguration((e) => e.affectsConfiguration("macroDeckBridge") ? restart(context) : undefined),
		{ dispose: () => client?.stop() },
	);

	watchState(context);
	watchEvents(context);
	await restart(context);
}

export function deactivate(): void {
	client?.stop();
	client = undefined;
}

async function pairingToken(context: vscode.ExtensionContext): Promise<string> {
	let token = await context.secrets.get(TOKEN_KEY);
	if (!token) {
		token = newPairingToken();
		await context.secrets.store(TOKEN_KEY, token);
	}

	return token;
}

async function restart(context: vscode.ExtensionContext): Promise<void> {
	client?.stop();
	client = undefined;

	const config = vscode.workspace.getConfiguration("macroDeckBridge");
	if (!config.get<boolean>("enabled", true)) {
		showStatus({ kind: "disabled" });
		return;
	}

	client = new BridgeClient({
		port: config.get<number>("port", 51741),
		token: await pairingToken(context),
		windowId,
		onRequest: handleRequest,
		onStatus: showStatus,
		onConnected: () => { sendStateNow(); void refreshGitBranch(); },
	});
	client.start();
}

function showStatus(status: ConnectionStatus): void {
	const text: Record<ConnectionStatus["kind"], [string, string]> = {
		disabled: ["$(circle-slash) Macro Deck", "Macro Deck Bridge is turned off in settings."],
		connecting: ["$(debug-disconnect) Macro Deck", "Waiting for Macro Deck. Is it running, with the VS Code Plugin set up?"],
		connected: ["$(plug) Macro Deck", "Connected to Macro Deck."],
		unpaired: ["$(warning) Macro Deck", "Macro Deck rejected the pairing token. Click to copy this VS Code's token, then paste it into the plugin's setup."],
		outdated: ["$(warning) Macro Deck", "This extension and the Macro Deck plugin speak different protocol versions. Update both."],
	};

	const [label, tooltip] = text[status.kind];
	statusItem.text = label;
	statusItem.tooltip = status.kind === "outdated" ? `${tooltip} (${status.reason})` : tooltip;
	statusItem.command = status.kind === "connected" ? undefined : "macroDeckBridge.copyPairingToken";
	statusItem.show();
}

// ---- Requests from Macro Deck ------------------------------------------------------------------

async function handleRequest(method: string, params: Record<string, unknown>): Promise<unknown> {
	switch (method) {
		case "executeCommand": {
			const command = String(params.command ?? "");
			const args = Array.isArray(params.args) ? params.args : [];
			// The result is ignored: commands can return anything, including objects that do not serialize.
			await vscode.commands.executeCommand(command, ...args);
			return null;
		}

		case "listCommands":
			return listCommands();

		case "listTasks": {
			const tasks = await vscode.tasks.fetchTasks();
			const seen = new Set<string>();
			return tasks
				.filter((t) => !seen.has(t.name) && seen.add(t.name))
				.map((t) => ({ id: t.name, label: t.source ? `${t.name} (${t.source})` : t.name }));
		}

		case "runTask": {
			const name = String(params.task ?? "");
			const task = (await vscode.tasks.fetchTasks()).find((t) => t.name === name);
			if (!task) {
				throw new Error(`No task named "${name}" in this workspace.`);
			}

			await vscode.tasks.executeTask(task);
			return null;
		}

		case "listTerminals":
			return vscode.window.terminals.map((t) => ({ id: t.name, label: t.name }));

		case "runInTerminal":
			return runInTerminal(
				String(params.commandLine ?? ""),
				String(params.terminal || "Macro Deck"),
				params.focus !== false,
				params.interrupt === true);

		case "listLaunchConfigs":
			return listLaunchConfigs();

		case "debug":
			return runDebugCommand(String(params.command ?? ""), typeof params.configuration === "string" ? params.configuration : undefined);

		default:
			throw new Error(`Unknown request "${method}". Update the Macro Deck Bridge extension.`);
	}
}

const SHELL_INTEGRATION_WAIT_MS = 3_000;
const INTERRUPT_SETTLE_MS = 500;

async function runInTerminal(commandLine: string, name: string, focus: boolean, interrupt: boolean): Promise<null> {
	if (!commandLine.trim()) {
		throw new Error("No command line to run.");
	}

	let terminal = vscode.window.terminals.find((t) => t.name === name && t.exitStatus === undefined);
	const created = !terminal;
	terminal ??= vscode.window.createTerminal({ name });

	if (focus) {
		terminal.show(false);
	}

	if (interrupt && !created) {
		terminal.sendText("\u0003", false);
		// The interrupted program needs a moment to exit and the shell to redraw its prompt.
		await new Promise((resolve) => setTimeout(resolve, INTERRUPT_SETTLE_MS));
	}

	// Shell integration runs the line as a real command (correct quoting, no typing into a half-drawn
	// prompt); a new terminal needs a moment before it reports it. Without it, type the text instead.
	const shell = terminal.shellIntegration ?? (created ? await waitForShellIntegration(terminal) : undefined);
	const lines = commandLine.split(/\r?\n/).filter((l) => l.trim().length > 0);
	if (shell && lines.length === 1) {
		shell.executeCommand(lines[0]);
	} else {
		terminal.sendText(lines.join("\n"), true);
	}

	return null;
}

function waitForShellIntegration(terminal: vscode.Terminal): Promise<vscode.TerminalShellIntegration | undefined> {
	return new Promise((resolve) => {
		const timer = setTimeout(() => { listener.dispose(); resolve(undefined); }, SHELL_INTEGRATION_WAIT_MS);
		const listener = vscode.window.onDidChangeTerminalShellIntegration((e) => {
			if (e.terminal === terminal) {
				clearTimeout(timer);
				listener.dispose();
				resolve(e.shellIntegration);
			}
		});
	});
}

const DEBUG_COMMANDS: Record<string, string> = {
	stop: "workbench.action.debug.stop",
	restart: "workbench.action.debug.restart",
	continue: "workbench.action.debug.continue",
	pause: "workbench.action.debug.pause",
	"step-over": "workbench.action.debug.stepOver",
	"step-into": "workbench.action.debug.stepInto",
	"step-out": "workbench.action.debug.stepOut",
};

async function runDebugCommand(command: string, configuration: string | undefined): Promise<null> {
	if (command === "toggle") {
		command = vscode.debug.activeDebugSession ? "stop" : "start";
	}

	if (command === "start") {
		if (!configuration) {
			// Whatever is selected in the Run and Debug view, exactly as F5 would.
			await vscode.commands.executeCommand("workbench.action.debug.start");
			return null;
		}

		const folder = vscode.workspace.workspaceFolders?.find((f) =>
			launchConfigNames(f).includes(configuration)) ?? vscode.workspace.workspaceFolders?.[0];
		if (!await vscode.debug.startDebugging(folder, configuration)) {
			throw new Error(`Could not start "${configuration}". Check that it exists in launch.json.`);
		}

		return null;
	}

	const id = DEBUG_COMMANDS[command];
	if (!id) {
		throw new Error(`Unknown debug command "${command}". Update the Macro Deck Bridge extension.`);
	}

	await vscode.commands.executeCommand(id);
	return null;
}

function launchConfigNames(folder?: vscode.WorkspaceFolder): string[] {
	const launch = vscode.workspace.getConfiguration("launch", folder?.uri);
	const names = (list: unknown) => Array.isArray(list)
		? list.map((c) => c?.name).filter((n): n is string => typeof n === "string")
		: [];
	return [...names(launch.get("configurations")), ...names(launch.get("compounds"))];
}

/** Launch configurations and compounds from every workspace folder's launch.json and the workspace file. */
function listLaunchConfigs(): { id: string; label: string }[] {
	const folders = vscode.workspace.workspaceFolders ?? [];
	const seen = new Set<string>();
	const result: { id: string; label: string }[] = [];
	for (const folder of folders.length > 0 ? folders : [undefined]) {
		for (const name of launchConfigNames(folder)) {
			if (!seen.has(name)) {
				seen.add(name);
				result.push({ id: name, label: folders.length > 1 && folder ? `${name} (${folder.name})` : name });
			}
		}
	}

	return result;
}

/** Every command id, labelled with its Command Palette title where an extension contributes one. */
async function listCommands(): Promise<{ id: string; label: string }[]> {
	const titles = new Map<string, string>();
	for (const extension of vscode.extensions.all) {
		const contributed = extension.packageJSON?.contributes?.commands;
		for (const c of Array.isArray(contributed) ? contributed : []) {
			const title = typeof c.title === "string" ? c.title : c.title?.value;
			if (typeof c.command === "string" && typeof title === "string" && !title.startsWith("%")) {
				titles.set(c.command, c.category ? `${typeof c.category === "string" ? c.category : c.category.value}: ${title}` : title);
			}
		}
	}

	const ids = await vscode.commands.getCommands(true);
	return ids
		.map((id) => ({ id, label: titles.has(id) ? `${titles.get(id)} (${id})` : id }))
		.sort((a, b) => a.label.localeCompare(b.label));
}

// ---- State pushed to Macro Deck ----------------------------------------------------------------

function watchState(context: vscode.ExtensionContext): void {
	context.subscriptions.push(
		vscode.window.onDidChangeWindowState(sendStateNow),
		vscode.window.onDidChangeActiveTextEditor(scheduleState),
		vscode.languages.onDidChangeDiagnostics(scheduleState),
		vscode.workspace.onDidChangeTextDocument((e) => e.contentChanges.length > 0 ? scheduleState() : undefined),
		vscode.workspace.onDidSaveTextDocument(scheduleState),
		vscode.workspace.onDidCloseTextDocument(scheduleState),
		vscode.workspace.onDidChangeWorkspaceFolders(scheduleState),
		vscode.debug.onDidChangeActiveDebugSession(scheduleState),
		vscode.debug.onDidChangeActiveStackItem(scheduleState),
		vscode.debug.onDidStartDebugSession(scheduleState),
		vscode.debug.onDidTerminateDebugSession(scheduleState),
		{ dispose: () => clearTimeout(stateTimer) },
	);

	watchGitBranch(context);
}

function scheduleState(): void {
	clearTimeout(stateTimer);
	stateTimer = setTimeout(sendStateNow, STATE_DEBOUNCE_MS);
}

function sendStateNow(): void {
	clearTimeout(stateTimer);
	if (!client?.connected) {
		return;
	}

	const document = vscode.window.activeTextEditor?.document;
	let errors = 0;
	let warnings = 0;
	for (const [, diagnostics] of vscode.languages.getDiagnostics()) {
		for (const d of diagnostics) {
			if (d.severity === vscode.DiagnosticSeverity.Error) {
				errors++;
			} else if (d.severity === vscode.DiagnosticSeverity.Warning) {
				warnings++;
			}
		}
	}

	client.send({
		type: "state",
		state: {
			focused: vscode.window.state.focused,
			appName: vscode.env.appName,
			workspace: vscode.workspace.name ?? null,
			// Only the file name: the full path never leaves VS Code.
			activeFile: document ? path.basename(document.fileName) : null,
			language: document?.languageId ?? null,
			gitBranch,
			errors,
			warnings,
			debugging: vscode.debug.activeDebugSession !== undefined,
			debugState: debugState(),
			unsavedFiles: vscode.workspace.textDocuments.filter((d) => d.isDirty).length,
		},
	});
}

/** A selected stack frame only exists while execution is stopped at a breakpoint or step, and VS Code
 * reports it in every window kind, unlike debug adapter trackers, which only see local adapters. */
function debugState(): "stopped" | "running" | "paused" {
	if (!vscode.debug.activeDebugSession) {
		return "stopped";
	}

	return vscode.debug.activeStackItem instanceof vscode.DebugStackFrame ? "paused" : "running";
}

// ---- Git branch ----------------------------------------------------------------------------------

const GIT_POLL_MS = 5_000;
let gitBranch: string | null = null;

/** Re-reads the branch every few seconds, so a switch made in a terminal shows up too. A HEAD read is
 * one tiny file, even in a remote window. */
function watchGitBranch(context: vscode.ExtensionContext): void {
	const refresh = () => void refreshGitBranch();
	const timer = setInterval(() => client?.connected ? refresh() : undefined, GIT_POLL_MS);
	context.subscriptions.push(
		{ dispose: () => clearInterval(timer) },
		vscode.window.onDidChangeActiveTextEditor(refresh),
		vscode.workspace.onDidChangeWorkspaceFolders(refresh),
	);
	refresh();
}

async function refreshGitBranch(): Promise<void> {
	let branch: string | null = null;
	try {
		branch = await readGitBranch();
	} catch {
		// No repository, or HEAD unreadable mid-checkout: report no branch.
	}

	if (branch !== gitBranch) {
		gitBranch = branch;
		scheduleState();
	}
}

/** The branch of the repository holding the active file, else of the first workspace folder. */
async function readGitBranch(): Promise<string | null> {
	const active = vscode.window.activeTextEditor?.document.uri;
	const folder = (active && vscode.workspace.getWorkspaceFolder(active)) ?? vscode.workspace.workspaceFolders?.[0];
	if (!folder) {
		return null;
	}

	// Walk up from the active file to the workspace folder, so nested repositories win.
	const root = folder.uri.path;
	let dir = active && active.path.startsWith(root) ? vscode.Uri.joinPath(active, "..") : folder.uri;
	while (dir.path.length >= root.length) {
		const gitDir = await findGitDir(dir);
		if (gitDir) {
			return branchFromHead(await readText(vscode.Uri.joinPath(gitDir, "HEAD")));
		}

		const parent = vscode.Uri.joinPath(dir, "..");
		if (parent.path === dir.path) {
			break;
		}

		dir = parent;
	}

	return null;
}

async function findGitDir(dir: vscode.Uri): Promise<vscode.Uri | null> {
	const dotGit = vscode.Uri.joinPath(dir, ".git");
	let stat: vscode.FileStat;
	try {
		stat = await vscode.workspace.fs.stat(dotGit);
	} catch {
		return null;
	}

	if (stat.type & vscode.FileType.Directory) {
		return dotGit;
	}

	const target = gitDirFromDotGitFile(await readText(dotGit));
	if (!target) {
		return null;
	}

	if (!target.startsWith("/") && !/^[a-z]:[\\/]/i.test(target)) {
		return vscode.Uri.joinPath(dir, target);
	}

	// Absolute: a local path for a local window, else a path on the same remote.
	return dir.scheme === "file" ? vscode.Uri.file(target) : dir.with({ path: target });
}

async function readText(uri: vscode.Uri): Promise<string> {
	return new TextDecoder().decode(await vscode.workspace.fs.readFile(uri));
}

// ---- Events forwarded to Macro Deck ------------------------------------------------------------

function watchEvents(context: vscode.ExtensionContext): void {
	const emit = (name: string, data: Record<string, unknown>) => client?.send({ type: "event", name, data });

	context.subscriptions.push(
		vscode.workspace.onDidSaveTextDocument((d) =>
			emit("file-saved", { fileName: path.basename(d.fileName), language: d.languageId })),
		vscode.debug.onDidStartDebugSession((s) => emit("debug-started", { sessionName: s.name })),
		vscode.debug.onDidTerminateDebugSession((s) => emit("debug-stopped", { sessionName: s.name })),
		vscode.tasks.onDidEndTaskProcess((e) =>
			emit("task-finished", { task: e.execution.task.name, exitCode: e.exitCode ?? null })),
	);
}
