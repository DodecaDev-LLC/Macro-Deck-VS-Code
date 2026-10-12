using System.Security.Cryptography;
using System.Text;

namespace DodecaDev.VsCode.Bridge;

/// <summary>
/// Mutual proof that both ends hold the pairing token, without either end ever sending it. The plugin
/// proves itself first, so a VS Code window never obeys a process that merely grabbed the port, and
/// the window proves itself second, so the deck never talks to a client the user did not pair.
/// </summary>
/// <remarks>Must stay byte-for-byte in step with <c>vscode-extension/src/auth.ts</c> at the repository root.</remarks>
internal static class BridgeAuth
{
	internal const string ServerRole = "server";
	internal const string ClientRole = "client";

	internal static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

	/// <summary>HMAC-SHA256 over <c>"{role}:{nonce}"</c>, keyed with the token, as lowercase hex.</summary>
	internal static string Proof(string token, string role, string nonce)
	{
		var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes($"{role}:{nonce}"));
		return Convert.ToHexStringLower(mac);
	}

	internal static bool Verify(string token, string role, string nonce, string? proof)
	{
		if (proof is null)
		{
			return false;
		}

		var expected = Encoding.ASCII.GetBytes(Proof(token, role, nonce));
		var actual = Encoding.ASCII.GetBytes(proof);
		return CryptographicOperations.FixedTimeEquals(expected, actual);
	}
}
