using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace DodecaDev.VsCode.Bridge;

/// <summary>
/// One authenticated VS Code window. Owns its socket: requests go out through <see cref="RequestAsync"/>,
/// and state reports and events come back through <see cref="RunAsync"/>.
/// </summary>
internal sealed class VsCodeWindow : IDisposable
{
	internal const int MaxMessageBytes = 4 * 1024 * 1024;

	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

	private readonly WebSocket _socket;
	private readonly ILogger _logger;
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
	private int _nextRequestId;

	internal VsCodeWindow(string id, WebSocket socket, ILogger logger)
	{
		Id = id;
		_socket = socket;
		_logger = logger;
	}

	internal string Id { get; }

	internal WindowState State { get; private set; } = WindowState.Empty;

	/// <summary>When the window last reported focus; picks the target when no window has focus now.</summary>
	internal DateTimeOffset LastFocused { get; private set; } = DateTimeOffset.MinValue;

	/// <summary>What a pin or a per-button target remembers this window by: its workspace, so the choice
	/// survives a window reload, or the window itself when it has no folder open.</summary>
	internal string TargetKey => State.WorkspaceKey is { Length: > 0 } key ? key : $"window:{Id}";

	internal event Action<VsCodeWindow>? StateChanged;

	/// <summary>The user ran "Control This Window" (true) or unpinned it (false) in this window.</summary>
	internal event Action<VsCodeWindow, bool>? PinRequested;

	internal event Action<VsCodeWindow, string, JsonObject?>? EventReceived;

	/// <summary>Sends one request and waits, bounded, for the window's answer.</summary>
	/// <exception cref="BridgeRequestException">The window answered with an error, timed out or went away.</exception>
	internal async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
	{
		var id = Interlocked.Increment(ref _nextRequestId);
		var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = completion;

		try
		{
			await SendAsync(new JsonObject
			{
				["type"] = "request",
				["id"] = id,
				["method"] = method,
				["params"] = parameters ?? new JsonObject()
			}, cancellationToken);

			return await completion.Task.WaitAsync(RequestTimeout, cancellationToken);
		}
		catch (TimeoutException)
		{
			throw new BridgeRequestException(BridgeFailure.Timeout, null);
		}
		catch (WebSocketException)
		{
			throw new BridgeRequestException(BridgeFailure.Disconnected, null);
		}
		finally
		{
			_pending.TryRemove(id, out _);
		}
	}

	internal async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
	{
		var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
		await _sendLock.WaitAsync(cancellationToken);
		try
		{
			await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	/// <summary>Reads until the window disconnects or <paramref name="cancellationToken"/> fires.</summary>
	internal async Task RunAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var message = await ReceiveJsonAsync(_socket, cancellationToken);
				if (message is null)
				{
					return;
				}

				Handle(message);
			}
		}
		catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
		{
			// A closed window or a stopping server; either way the loop is over.
		}
		finally
		{
			foreach (var pending in _pending.Values)
			{
				pending.TrySetException(new BridgeRequestException(BridgeFailure.Disconnected, null));
			}
		}
	}

	public void Dispose() => _sendLock.Dispose();

	internal async Task CloseAsync()
	{
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
			await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Macro Deck is stopping", timeout.Token);
		}
		catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
		{
			_socket.Abort();
		}
	}

	private void Handle(JsonObject message)
	{
		switch ((string?)message["type"])
		{
			case "state":
				var state = message["state"]?.Deserialize<WindowState>() ?? WindowState.Empty;
				if (state.Focused)
				{
					LastFocused = DateTimeOffset.UtcNow;
				}

				State = state;
				StateChanged?.Invoke(this);
				break;

			case "response" when message["id"]?.GetValue<int>() is { } id && _pending.TryGetValue(id, out var completion):
				if (message["ok"]?.GetValue<bool>() == true)
				{
					completion.TrySetResult(message["result"]?.DeepClone());
				}
				else
				{
					completion.TrySetException(new BridgeRequestException(BridgeFailure.Rejected,
						(string?)message["error"]?["message"]));
				}

				break;

			case "pin":
				PinRequested?.Invoke(this, message["pinned"]?.GetValue<bool>() ?? true);
				break;

			case "event" when message["name"]?.GetValue<string>() is { Length: > 0 } name:
				EventReceived?.Invoke(this, name, message["data"] as JsonObject);
				break;

			default:
				_logger.Debug("Ignored a {Type} message from window {WindowId}", (string?)message["type"], Id);
				break;
		}
	}

	/// <summary>One whole text message as JSON; <c>null</c> once the peer closes.</summary>
	internal static async Task<JsonObject?> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken)
	{
		var buffer = new byte[16 * 1024];
		using var stream = new MemoryStream();
		while (true)
		{
			var result = await socket.ReceiveAsync(buffer, cancellationToken);
			if (result.MessageType == WebSocketMessageType.Close)
			{
				return null;
			}

			stream.Write(buffer, 0, result.Count);
			if (stream.Length > MaxMessageBytes)
			{
				throw new WebSocketException(WebSocketError.Faulted, "Message too large.");
			}

			if (result.EndOfMessage)
			{
				break;
			}
		}

		try
		{
			return JsonNode.Parse(stream.ToArray()) as JsonObject;
		}
		catch (JsonException)
		{
			return new JsonObject();
		}
	}
}

internal enum BridgeFailure
{
	NoWindow,
	Disconnected,
	Timeout,
	Rejected
}

internal sealed class BridgeRequestException(BridgeFailure failure, string? detail)
	: Exception(detail ?? failure.ToString())
{
	internal BridgeFailure Failure { get; } = failure;

	/// <summary>The window's own error text (for example VS Code's "command 'x' not found").</summary>
	internal string? Detail { get; } = detail;
}
