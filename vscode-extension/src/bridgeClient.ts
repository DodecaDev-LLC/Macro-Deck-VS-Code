import WebSocket from "ws";
import { CLIENT_ROLE, SERVER_ROLE, newNonce, proof, verify } from "./auth";

// The window's side of the bridge. Deliberately free of the vscode module, so it can be tested in
// plain Node against a fake plugin.

export const PROTOCOL_VERSION = 1;
export const BRIDGE_PATH = "/macro-deck-bridge";

/** Close codes the plugin sends when it refuses a window. */
export const CloseCodes = {
	BadHandshake: 4000,
	PairingMismatch: 4001,
	UnsupportedProtocol: 4002,
} as const;

export type ConnectionStatus =
	| { kind: "disabled" }
	| { kind: "connecting" }
	| { kind: "connected" }
	| { kind: "unpaired" }
	| { kind: "outdated"; reason: string };

export type RequestHandler = (method: string, params: Record<string, unknown>) => Promise<unknown>;

export interface BridgeClientOptions {
	host?: string;
	port: number;
	token: string;
	windowId: string;
	onRequest: RequestHandler;
	onStatus: (status: ConnectionStatus) => void;
	/** Called on every (re)connect, so the plugin starts from the window's current state. */
	onConnected: () => void;
}

const MIN_RETRY_MS = 1_000;
const MAX_RETRY_MS = 30_000;
const HANDSHAKE_TIMEOUT_MS = 5_000;

export class BridgeClient {
	private socket: WebSocket | undefined;
	private retryTimer: NodeJS.Timeout | undefined;
	private retryMs = MIN_RETRY_MS;
	private authenticated = false;
	private stopped = true;

	constructor(private readonly options: BridgeClientOptions) {}

	start(): void {
		this.stopped = false;
		this.connect();
	}

	stop(): void {
		this.stopped = true;
		clearTimeout(this.retryTimer);
		this.socket?.removeAllListeners();
		this.socket?.on("error", () => {});
		this.socket?.close(1000, "VS Code window closing");
		this.socket = undefined;
		this.authenticated = false;
	}

	get connected(): boolean {
		return this.authenticated;
	}

	/** Sends a message if the plugin has accepted this window; drops it otherwise. */
	send(message: Record<string, unknown>): void {
		if (this.authenticated && this.socket?.readyState === WebSocket.OPEN) {
			this.socket.send(JSON.stringify(message));
		}
	}

	private connect(): void {
		if (this.stopped) {
			return;
		}

		this.options.onStatus({ kind: "connecting" });
		const host = this.options.host ?? "127.0.0.1";
		const socket = new WebSocket(`ws://${host}:${this.options.port}${BRIDGE_PATH}`, {
			handshakeTimeout: HANDSHAKE_TIMEOUT_MS,
			maxPayload: 1024 * 1024,
		});
		this.socket = socket;

		const clientNonce = newNonce();
		let serverNonce: string | undefined;
		const handshakeTimer = setTimeout(() => socket.terminate(), HANDSHAKE_TIMEOUT_MS * 2);

		socket.on("open", () => {
			socket.send(JSON.stringify({ type: "hello", protocol: PROTOCOL_VERSION, nonce: clientNonce }));
		});

		socket.on("message", (data) => {
			let message: Record<string, unknown>;
			try {
				message = JSON.parse(data.toString());
			} catch {
				return;
			}

			if (!this.authenticated) {
				if (message.type === "challenge") {
					// The plugin proves it knows the token before this window obeys anything it sends.
					if (!verify(this.options.token, SERVER_ROLE, clientNonce, message.proof)) {
						this.giveUp(socket, { kind: "unpaired" });
						return;
					}

					serverNonce = String(message.nonce);
					socket.send(JSON.stringify({
						type: "auth",
						proof: proof(this.options.token, CLIENT_ROLE, serverNonce),
						windowId: this.options.windowId,
					}));
				} else if (message.type === "welcome" && serverNonce) {
					clearTimeout(handshakeTimer);
					this.authenticated = true;
					this.retryMs = MIN_RETRY_MS;
					this.options.onStatus({ kind: "connected" });
					this.options.onConnected();
				}

				return;
			}

			if (message.type === "request") {
				void this.answer(socket, message);
			}
		});

		socket.on("close", (code, reason) => {
			clearTimeout(handshakeTimer);
			this.authenticated = false;
			if (this.socket !== socket || this.stopped) {
				return;
			}

			if (code === CloseCodes.PairingMismatch) {
				this.options.onStatus({ kind: "unpaired" });
			} else if (code === CloseCodes.UnsupportedProtocol) {
				this.options.onStatus({ kind: "outdated", reason: reason.toString() });
			} else {
				this.options.onStatus({ kind: "connecting" });
			}

			// Keep retrying even after a pairing mismatch: the user may fix the token on the deck side.
			this.scheduleRetry(code === CloseCodes.PairingMismatch ? MAX_RETRY_MS : undefined);
		});

		// Errors are followed by close, which handles the retry; Macro Deck not running is the normal case.
		socket.on("error", () => {});
	}

	private giveUp(socket: WebSocket, status: ConnectionStatus): void {
		this.options.onStatus(status);
		socket.removeAllListeners();
		socket.on("error", () => {});
		socket.terminate();
		this.scheduleRetry(MAX_RETRY_MS);
	}

	private scheduleRetry(delayMs?: number): void {
		if (this.stopped) {
			return;
		}

		clearTimeout(this.retryTimer);
		const delay = delayMs ?? this.retryMs;
		this.retryMs = Math.min(this.retryMs * 2, MAX_RETRY_MS);
		this.retryTimer = setTimeout(() => this.connect(), delay);
	}

	private async answer(socket: WebSocket, message: Record<string, unknown>): Promise<void> {
		const id = message.id;
		const params = (message.params ?? {}) as Record<string, unknown>;
		let reply: Record<string, unknown>;
		try {
			const result = await this.options.onRequest(String(message.method), params);
			reply = { type: "response", id, ok: true, result: result ?? null };
		} catch (error) {
			reply = { type: "response", id, ok: false, error: { message: error instanceof Error ? error.message : String(error) } };
		}

		if (socket.readyState === WebSocket.OPEN) {
			socket.send(JSON.stringify(reply));
		}
	}
}
