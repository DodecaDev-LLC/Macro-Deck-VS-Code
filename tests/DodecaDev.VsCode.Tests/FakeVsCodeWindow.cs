using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DodecaDev.VsCode.Tests;

/// <summary>
/// Plays the companion extension's side of the bridge protocol. The proof is computed here on its own,
/// not through the plugin's BridgeAuth, so the tests pin the wire format the TypeScript side also follows.
/// </summary>
internal sealed class FakeVsCodeWindow : IAsyncDisposable
{
	private readonly ClientWebSocket _socket = new();

	private FakeVsCodeWindow() { }

	internal WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

	internal static int FreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	/// <summary>Connects and runs the handshake; returns whether the plugin welcomed the window.</summary>
	internal static async Task<(FakeVsCodeWindow Window, bool Welcomed)> ConnectAsync(int port, string token,
		string windowId = "window-1", Action<ClientWebSocketOptions>? configure = null)
	{
		var window = new FakeVsCodeWindow();
		configure?.Invoke(window._socket.Options);
		await window._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/macro-deck-bridge"), Timeout());

		var clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
		await window.SendAsync(new JsonObject { ["type"] = "hello", ["protocol"] = 1, ["nonce"] = clientNonce });

		var challenge = await window.ReceiveAsync();
		if (challenge is null || (string?)challenge["proof"] != Proof(token, "server", clientNonce))
		{
			return (window, false);
		}

		await window.SendAsync(new JsonObject
		{
			["type"] = "auth",
			["proof"] = Proof(token, "client", (string)challenge["nonce"]!),
			["windowId"] = windowId
		});

		var welcome = await window.ReceiveAsync();
		return (window, (string?)welcome?["type"] == "welcome");
	}

	internal static string Proof(string token, string role, string nonce)
		=> Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes($"{role}:{nonce}")));

	internal Task SendStateAsync(JsonObject state)
		=> SendAsync(new JsonObject { ["type"] = "state", ["state"] = state });

	internal Task SendEventAsync(string name, JsonObject data)
		=> SendAsync(new JsonObject { ["type"] = "event", ["name"] = name, ["data"] = data });

	/// <summary>Waits for the plugin's next request and answers it.</summary>
	internal async Task<JsonObject> AnswerNextRequestAsync(JsonNode? result = null, string? error = null)
	{
		var request = await ReceiveAsync() ?? throw new InvalidOperationException("The plugin closed the socket.");
		await SendAsync(error is null
			? new JsonObject { ["type"] = "response", ["id"] = request["id"]!.GetValue<int>(), ["ok"] = true, ["result"] = result }
			: new JsonObject { ["type"] = "response", ["id"] = request["id"]!.GetValue<int>(), ["ok"] = false, ["error"] = new JsonObject { ["message"] = error } });
		return request;
	}

	internal async Task SendAsync(JsonObject message)
		=> await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, Timeout());

	internal async Task<JsonObject?> ReceiveAsync()
	{
		var buffer = new byte[64 * 1024];
		using var stream = new MemoryStream();
		WebSocketReceiveResult result;
		do
		{
			result = await _socket.ReceiveAsync(buffer, Timeout());
			if (result.MessageType == WebSocketMessageType.Close)
			{
				return null;
			}

			stream.Write(buffer, 0, result.Count);
		}
		while (!result.EndOfMessage);

		return JsonNode.Parse(stream.ToArray()) as JsonObject;
	}

	public async ValueTask DisposeAsync()
	{
		if (_socket.State == WebSocketState.Open)
		{
			try
			{
				await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Timeout());
			}
			catch (WebSocketException)
			{
			}
		}

		_socket.Dispose();
	}

	private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;
}
