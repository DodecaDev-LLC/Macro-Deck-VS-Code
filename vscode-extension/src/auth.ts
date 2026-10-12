import { createHmac, randomBytes, timingSafeEqual } from "node:crypto";

// Must stay byte-for-byte in step with macro-deck-plugin/src/DodecaDev.VsCode/Bridge/BridgeAuth.cs.

export const SERVER_ROLE = "server";
export const CLIENT_ROLE = "client";

export function newNonce(): string {
	return randomBytes(32).toString("base64");
}

export function newPairingToken(): string {
	return randomBytes(24).toString("base64url");
}

/** HMAC-SHA256 over `${role}:${nonce}`, keyed with the pairing token, as lowercase hex. */
export function proof(token: string, role: string, nonce: string): string {
	return createHmac("sha256", token).update(`${role}:${nonce}`).digest("hex");
}

export function verify(token: string, role: string, nonce: string, actual: unknown): boolean {
	if (typeof actual !== "string") {
		return false;
	}

	const expected = Buffer.from(proof(token, role, nonce));
	const received = Buffer.from(actual);
	return expected.length === received.length && timingSafeEqual(expected, received);
}
