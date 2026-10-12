// Runs the window's side of the bridge against a fake plugin. The C# suite pins the same proof vector
// (BridgeAuthTests), so the two implementations cannot drift apart unnoticed.
const test = require("node:test");
const assert = require("node:assert/strict");
const { WebSocketServer } = require("ws");
const { proof, verify } = require("../out/auth.js");
const { BridgeClient } = require("../out/bridgeClient.js");

const TOKEN = "test-pairing-token-0123456789";

test("the proof matches the vector the plugin's tests pin", () => {
	assert.equal(proof(TOKEN, "server", "nonce-0123456789abcdef"),
		"37e088aa698f8dcbaf966d769cade228a12ff3c66a14428eeb14565d4d58d2de");
	assert.equal(verify(TOKEN, "server", "nonce-0123456789abcdef", "00"), false);
});

/** A minimal stand-in for the plugin's BridgeServer handshake. */
function fakePlugin(serverToken, onWindow) {
	const server = new WebSocketServer({ host: "127.0.0.1", port: 0, path: "/macro-deck-bridge" });
	server.on("connection", (socket) => {
		const serverNonce = "server-nonce-0123456789";
		socket.once("message", (data) => {
			const hello = JSON.parse(data.toString());
			socket.send(JSON.stringify({ type: "challenge", nonce: serverNonce, proof: proof(serverToken, "server", hello.nonce) }));
			socket.once("message", (data2) => {
				const auth = JSON.parse(data2.toString());
				if (!verify(serverToken, "client", serverNonce, auth.proof)) {
					socket.close(4001, "Pairing token mismatch");
					return;
				}
				socket.send(JSON.stringify({ type: "welcome" }));
				onWindow(socket);
			});
		});
	});
	return new Promise((resolve) => server.on("listening", () => resolve(server)));
}

function client(port, token, onRequest = async () => null) {
	const statuses = [];
	const c = new BridgeClient({
		port, token, windowId: "w1", onRequest,
		onStatus: (s) => statuses.push(s.kind),
		onConnected: () => c.send({ type: "state", state: { focused: true, errors: 2 } }),
	});
	return { c, statuses };
}

test("a paired window reports its state and answers requests", async () => {
	const messages = [];
	let window;
	const server = await fakePlugin(TOKEN, (socket) => {
		window = socket;
		socket.on("message", (d) => messages.push(JSON.parse(d.toString())));
	});
	const { c, statuses } = client(server.address().port, TOKEN, async (method, params) => ({ method, params }));
	c.start();

	await waitFor(() => messages.some((m) => m.type === "state"));
	window.send(JSON.stringify({ type: "request", id: 7, method: "executeCommand", params: { command: "x" } }));
	await waitFor(() => messages.some((m) => m.type === "response"));

	const response = messages.find((m) => m.type === "response");
	assert.deepEqual(response, { type: "response", id: 7, ok: true, result: { method: "executeCommand", params: { command: "x" } } });
	assert.ok(statuses.includes("connected"));
	c.stop();
	server.close();
});

test("a window never trusts a plugin with a different token", async () => {
	let welcomed = false;
	const server = await fakePlugin("a-different-token-0123456789", () => { welcomed = true; });
	const { c, statuses } = client(server.address().port, TOKEN);
	c.start();

	await waitFor(() => statuses.includes("unpaired"));
	assert.equal(welcomed, false);
	assert.equal(c.connected, false);
	c.stop();
	server.close();
});

async function waitFor(condition, timeoutMs = 3000) {
	const started = Date.now();
	while (!condition()) {
		if (Date.now() - started > timeoutMs) throw new Error("Timed out");
		await new Promise((r) => setTimeout(r, 10));
	}
}
