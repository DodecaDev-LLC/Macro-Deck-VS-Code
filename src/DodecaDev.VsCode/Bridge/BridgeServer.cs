using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Serilog;

namespace DodecaDev.VsCode.Bridge;

/// <summary>
/// The loopback WebSocket hub every VS Code window's companion extension connects to, on a fixed,
/// user-chosen port separate from the listener Macro Deck assigns to the plugin, because the extension
/// has to know where to find it before anything else happens.
/// </summary>
/// <remarks>
/// A bare <see cref="TcpListener"/> doing the HTTP upgrade itself, not a second ASP.NET Core host: a
/// second <c>WebApplication</c> built inside the plugin process blocked synchronously on start under a
/// real Macro Deck host, stalling the SDK's whole integration lifecycle with it.
/// </remarks>
public sealed class BridgeServer : IAsyncDisposable
{
	internal const int ProtocolVersion = 1;
	internal const int DefaultPort = 51741;
	internal const string Path = "/macro-deck-bridge";

	/// <summary>Per-button target values; anything else names a workspace.</summary>
	internal const string TargetActive = "active";
	internal const string TargetAll = "all";

	private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
	private const int MaxRequestHeaderBytes = 8 * 1024;

	private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

	private readonly ILogger _logger;
	private readonly ConcurrentDictionary<string, VsCodeWindow> _windows = new();
	private readonly ConcurrentDictionary<Task, byte> _connections = new();
	private readonly SemaphoreSlim _lifecycle = new(1, 1);

	private TcpListener? _listener;
	private Task? _acceptLoop;
	private CancellationTokenSource? _stopping;
	private int _port;
	private string _token = "";
	private readonly Lock _targetLock = new();
	private (string? WindowId, bool Pinned)? _lastTarget;

	public BridgeServer(ILogger logger) => _logger = logger.ForContext<BridgeServer>();

	/// <summary>The configured port was taken by another process when the server last tried to start.</summary>
	internal bool PortInUse { get; private set; }

	internal int Port => _port;

	internal IReadOnlyCollection<VsCodeWindow> Windows => [.. _windows.Values];

	/// <summary>The window actions go to by default: the pinned one while it is open, else the focused one,
	/// else the one focused most recently.</summary>
	internal VsCodeWindow? ActiveWindow
	{
		get
		{
			var pinned = PinnedKey is { } key ? MostRecent(_windows.Values.Where(w => w.TargetKey == key)) : null;
			return pinned ?? MostRecent(_windows.Values);
		}
	}

	/// <summary>The workspace (or window) the user pinned. Kept while the pinned window is closed, so
	/// reloading or reopening that workspace makes it the target again.</summary>
	internal string? PinnedKey { get; private set; }

	/// <summary>Whether <see cref="ActiveWindow"/> is the pinned window rather than the last focused one.</summary>
	internal bool ActiveIsPinned => PinnedKey is { } key && ActiveWindow?.TargetKey == key;

	/// <summary>Windows in a stable order for cycling and pickers: by workspace name, then by key.</summary>
	internal IReadOnlyList<VsCodeWindow> OrderedWindows => [.. _windows.Values
		.OrderBy(w => w.State.Workspace ?? "", StringComparer.OrdinalIgnoreCase)
		.ThenBy(w => w.TargetKey, StringComparer.Ordinal)];

	/// <summary>A window connected, disconnected or reported new state.</summary>
	internal event Action? Changed;

	internal event Action<VsCodeWindow, string, JsonObject?>? EventReceived;

	/// <summary>Starts listening, or restarts when the port or token changed. Safe to call repeatedly.</summary>
	internal async Task StartAsync(int port, string token, CancellationToken cancellationToken = default)
	{
		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			if (_listener is not null && port == _port && token == _token)
			{
				return;
			}

			await StopCoreAsync();

			_port = port;
			_token = token;

			var listener = new TcpListener(IPAddress.Loopback, port);
			if (OperatingSystem.IsWindows())
			{
				// Without this, Windows lets another process bind the same port alongside us.
				listener.ExclusiveAddressUse = true;
			}

			try
			{
				listener.Start();
			}
			catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
			{
				PortInUse = true;
				_logger.Warning(ex, "Port {Port} is already in use; VS Code windows cannot connect", port);
				return;
			}

			PortInUse = false;
			_listener = listener;
			_stopping = new CancellationTokenSource();
			_acceptLoop = AcceptLoopAsync(listener, _stopping.Token);
			_logger.Information("Listening for VS Code windows on 127.0.0.1:{Port}", port);
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	internal async Task StopAsync()
	{
		await _lifecycle.WaitAsync();
		try
		{
			await StopCoreAsync();
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	/// <summary>Pins the window or workspace with this key; <c>null</c> unpins.</summary>
	internal void Pin(string? key)
	{
		PinnedKey = key;
		_logger.Information(key is null ? "Unpinned; following the last focused VS Code window" : "Pinned VS Code target {Key}", key);
		RaiseChanged();
	}

	/// <summary>Pins the window after the current target, wrapping around; returns it, or <c>null</c> with
	/// no window connected.</summary>
	internal VsCodeWindow? PinNext()
	{
		var windows = OrderedWindows;
		if (windows.Count == 0)
		{
			return null;
		}

		var current = ActiveWindow;
		var index = current is null ? -1 : windows.ToList().FindIndex(w => w.Id == current.Id);
		var next = windows[(index + 1) % windows.Count];
		Pin(next.TargetKey);
		return next;
	}

	/// <summary>The windows a button with this target setting acts on: <see cref="TargetActive"/> or empty
	/// for <see cref="ActiveWindow"/>, <see cref="TargetAll"/> for every window, otherwise the open window(s)
	/// of that workspace, matched by key or, for a typed value, by workspace name.</summary>
	internal IReadOnlyList<VsCodeWindow> ResolveTargets(string? target)
	{
		var value = target?.Trim();
		if (string.IsNullOrEmpty(value) || value == TargetActive)
		{
			return ActiveWindow is { } active ? [active] : [];
		}

		if (value == TargetAll)
		{
			return OrderedWindows;
		}

		var byKey = _windows.Values.Where(w => w.TargetKey == value).ToList();
		var matches = byKey.Count > 0
			? byKey
			: [.. _windows.Values.Where(w => string.Equals(w.State.Workspace, value, StringComparison.OrdinalIgnoreCase))];
		return MostRecent(matches) is { } window ? [window] : [];
	}

	/// <summary>Sends a request to the window(s) <paramref name="target"/> resolves to (see
	/// <see cref="ResolveTargets"/>). With several windows it answers with the first window's result and
	/// fails if any window failed.</summary>
	/// <exception cref="BridgeRequestException">No matching window is connected, or the request failed.</exception>
	internal async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken,
		string? target = null)
	{
		var windows = ResolveTargets(target);
		if (windows.Count == 0)
		{
			// A named workspace that is not open says so, by name; otherwise nothing is connected at all.
			var named = target?.Trim() is { Length: > 0 } t && t != TargetActive && t != TargetAll ? t : null;
			throw new BridgeRequestException(BridgeFailure.NoWindow, named is null ? null : FriendlyName(named));
		}

		if (windows.Count == 1)
		{
			return await windows[0].RequestAsync(method, parameters, cancellationToken);
		}

		var requests = windows.Select(w => w.RequestAsync(method, (JsonObject?)parameters?.DeepClone(), cancellationToken)).ToList();
		try
		{
			await Task.WhenAll(requests);
		}
		catch (BridgeRequestException)
		{
			// Reported below from the first window that failed, in a stable order.
		}

		var failed = requests.FirstOrDefault(r => r.IsFaulted);
		return failed is not null
			? throw failed.Exception!.InnerException ?? failed.Exception
			: await requests[0];
	}

	public async ValueTask DisposeAsync() => await StopAsync();

	private async Task StopCoreAsync()
	{
		if (_listener is null)
		{
			return;
		}

		await _stopping!.CancelAsync();
		_listener.Stop();
		await Task.WhenAll(_windows.Values.Select(w => w.CloseAsync()));

		// Bounded: a connection stuck mid-handshake is cut off by its own timeout anyway.
		await Task.WhenAll([_acceptLoop!, .. _connections.Keys]).WaitAsync(TimeSpan.FromSeconds(3))
			.ContinueWith(_ => { }, TaskScheduler.Default);

		_listener = null;
		_acceptLoop = null;
		_stopping.Dispose();
		_stopping = null;
		_windows.Clear();
		_lastTarget = null;
		Changed?.Invoke();
	}

	private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stopping)
	{
		while (!stopping.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync(stopping);
			}
			catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
			{
				return;
			}

			var connection = HandleConnectionAsync(client, stopping);
			_connections[connection] = 0;
			_ = connection.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
		}
	}

	private async Task HandleConnectionAsync(TcpClient client, CancellationToken stopping)
	{
		using var _ = client;
		try
		{
			var stream = client.GetStream();
			using var socket = await UpgradeAsync(stream, stopping);
			if (socket is null)
			{
				return;
			}

			await RunWindowAsync(socket, stopping);
		}
		catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or WebSocketException or ObjectDisposedException)
		{
			// The window went away or the server is stopping.
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "A VS Code connection failed unexpectedly");
		}
	}

	/// <summary>Reads the HTTP upgrade request and answers it per RFC 6455; <c>null</c> after refusing it.</summary>
	private static async Task<WebSocket?> UpgradeAsync(NetworkStream stream, CancellationToken stopping)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
		timeout.CancelAfter(HandshakeTimeout);

		var request = await ReadRequestHeadAsync(stream, timeout.Token);
		var lines = request?.Split("\r\n") ?? [];
		var headers = lines.Skip(1)
			.Select(l => l.Split(':', 2))
			.Where(p => p.Length == 2)
			.GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);

		var requestLine = lines.FirstOrDefault()?.Split(' ') ?? [];
		var isUpgrade = requestLine is ["GET", Path, _]
			&& headers.GetValueOrDefault("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true
			&& headers.GetValueOrDefault("Sec-WebSocket-Version") == "13"
			&& headers.ContainsKey("Sec-WebSocket-Key");

		// Browsers always send Origin and VS Code's extension host never does, so this keeps any web page
		// the user has open from talking to the bridge at all.
		if (!isUpgrade || headers.ContainsKey("Origin"))
		{
			await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
			return null;
		}

		// RFC 6455 fixes SHA-1 here; it proves the server understood the upgrade, nothing about security.
#pragma warning disable CA5350
		var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + WebSocketGuid)));
#pragma warning restore CA5350
		var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);

		return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
		{
			IsServer = true,
			KeepAliveInterval = TimeSpan.FromSeconds(20)
		});
	}

	/// <summary>Reads byte by byte up to the blank line, so no WebSocket frame bytes are consumed.</summary>
	private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		var buffer = new byte[MaxRequestHeaderBytes];
		var length = 0;
		while (length < buffer.Length)
		{
			if (await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken) == 0)
			{
				return null;
			}

			length++;
			if (length >= 4 && buffer.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8))
			{
				return Encoding.ASCII.GetString(buffer, 0, length - 4);
			}
		}

		return null;
	}

	private async Task RunWindowAsync(WebSocket socket, CancellationToken stopping)
	{
		var windowId = await HandshakeAsync(socket, stopping);
		if (windowId is null)
		{
			return;
		}

		using var window = new VsCodeWindow(windowId, socket, _logger);
		window.StateChanged += _ => RaiseChanged();
		window.EventReceived += (w, name, data) => EventReceived?.Invoke(w, name, data);
		window.PinRequested += (w, pinned) =>
		{
			if (pinned)
			{
				Pin(w.TargetKey);
			}
			else if (PinnedKey == w.TargetKey)
			{
				Pin(null);
			}
		};

		if (_windows.TryRemove(windowId, out var replaced))
		{
			await replaced.CloseAsync();
		}

		_windows[windowId] = window;
		_logger.Information("VS Code window {WindowId} connected", windowId);
		_lastTarget = null;   // a new window needs to hear the current target too
		RaiseChanged();

		try
		{
			await window.RunAsync(stopping);
		}
		finally
		{
			_windows.TryRemove(new KeyValuePair<string, VsCodeWindow>(windowId, window));
			_logger.Information("VS Code window {WindowId} disconnected", windowId);
			RaiseChanged();
		}
	}

	/// <summary>The last path segment of a workspace key (<c>.../MacroDeck3</c> → <c>MacroDeck3</c>), or
	/// the value itself when it is already a name.</summary>
	internal static string FriendlyName(string target)
	{
		var trimmed = target.TrimEnd('/');
		var slash = trimmed.LastIndexOf('/');
		var name = slash >= 0 ? Uri.UnescapeDataString(trimmed[(slash + 1)..]) : trimmed;
		return name.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase) ? name[..^".code-workspace".Length] : name;
	}

	private static VsCodeWindow? MostRecent(IEnumerable<VsCodeWindow> windows) => windows
		.OrderByDescending(w => w.State.Focused)
		.ThenByDescending(w => w.LastFocused)
		.FirstOrDefault();

	/// <summary>Raises <see cref="Changed"/> and, when the target moved, tells every window, so each status
	/// bar can show whether Macro Deck controls it.</summary>
	private void RaiseChanged()
	{
		Changed?.Invoke();

		var active = ActiveWindow;
		var target = (active?.Id, ActiveIsPinned);
		lock (_targetLock)
		{
			if (_lastTarget == target)
			{
				return;
			}

			_lastTarget = target;
		}

		var message = new JsonObject
		{
			["type"] = "target",
			["windowId"] = active?.Id,
			["workspace"] = active?.State.Workspace,
			["pinned"] = ActiveIsPinned
		};
		foreach (var window in _windows.Values)
		{
			_ = SendQuietlyAsync(window, (JsonObject)message.DeepClone());
		}
	}

	private static async Task SendQuietlyAsync(VsCodeWindow window, JsonObject message)
	{
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await window.SendAsync(message, timeout.Token);
		}
		catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
		{
			// The window is going away; its disconnect recomputes the target for the rest.
		}
	}

	/// <summary>Runs the mutual proof from <see cref="BridgeAuth"/>; returns the window id, or <c>null</c>
	/// after closing a socket that failed it.</summary>
	private async Task<string?> HandshakeAsync(WebSocket socket, CancellationToken stopping)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
		timeout.CancelAfter(HandshakeTimeout);

		try
		{
			var hello = await VsCodeWindow.ReceiveJsonAsync(socket, timeout.Token);
			if ((string?)hello?["type"] != "hello" || (string?)hello["nonce"] is not { Length: >= 16 } clientNonce)
			{
				await RejectAsync(socket, 4000, "Expected hello");
				return null;
			}

			if (hello["protocol"]?.GetValue<int>() != ProtocolVersion)
			{
				await RejectAsync(socket, 4002, $"Unsupported protocol; this plugin speaks {ProtocolVersion}");
				return null;
			}

			var serverNonce = BridgeAuth.NewNonce();
			await SendAsync(socket, new JsonObject
			{
				["type"] = "challenge",
				["nonce"] = serverNonce,
				["proof"] = BridgeAuth.Proof(_token, BridgeAuth.ServerRole, clientNonce)
			}, timeout.Token);

			var auth = await VsCodeWindow.ReceiveJsonAsync(socket, timeout.Token);
			if ((string?)auth?["type"] != "auth"
				|| !BridgeAuth.Verify(_token, BridgeAuth.ClientRole, serverNonce, (string?)auth["proof"]))
			{
				_logger.Warning("A VS Code window failed the pairing check; its pairing token does not match");
				await RejectAsync(socket, 4001, "Pairing token mismatch");
				return null;
			}

			await SendAsync(socket, new JsonObject { ["type"] = "welcome" }, timeout.Token);
			return (string?)auth["windowId"] is { Length: > 0 and <= 128 } id ? id : Guid.NewGuid().ToString("N");
		}
		catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException or FormatException)
		{
			socket.Abort();
			return null;
		}
	}

	private static async Task SendAsync(WebSocket socket, JsonObject message, CancellationToken cancellationToken)
		=> await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(message.ToJsonString()),
			WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

	private static async Task RejectAsync(WebSocket socket, int code, string reason)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		await socket.CloseAsync((WebSocketCloseStatus)code, reason, timeout.Token);
	}
}
