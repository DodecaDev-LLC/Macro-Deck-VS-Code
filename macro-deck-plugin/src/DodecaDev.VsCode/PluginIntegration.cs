using System.Globalization;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Actions;
using DodecaDev.VsCode.Bridge;
using DodecaDev.VsCode.ConfigFlow;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Events;
using MacroDeck.Sdk.Issues;
using MacroDeck.Sdk.Variables;
using Serilog;

namespace DodecaDev.VsCode;

/// <summary>
/// The VS Code integration. Every capability reads the same <see cref="BridgeServer"/>: actions go to the
/// window each button targets, variables report on the default window, and events are forwarded from any window.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, IVariableProvider, IEventProvider,
	IConfigFlowProvider, IIntegrationIssueProvider
{
	/// <summary>Values of <c>vscode_bridge_status</c>: where the bridge got to, readable in Macro Deck
	/// without any log access. Stable machine values, so buttons and conditions can test them.</summary>
	internal static class BridgeStatus
	{
		internal const string NotStarted = "not-started";
		internal const string ReadingConfig = "reading-config";
		internal const string NotConfigured = "not-configured";
		internal const string StartingListener = "starting-listener";
		internal const string Listening = "listening";
		internal const string PortInUse = "port-in-use";
		internal const string Error = "error";
	}

	internal const string PortInUseIssueId = "port-in-use";

	/// <summary>Events the extension may send, with the payload keys forwarded for each. Anything else
	/// from the extension is dropped, so a newer extension cannot publish events this plugin never declared.</summary>
	private static readonly Dictionary<string, string[]> ForwardedEvents = new Dictionary<string, string[]>
	{
		["file-saved"] = ["fileName", "language"],
		["debug-started"] = ["sessionName"],
		["debug-stopped"] = ["sessionName"],
		["task-finished"] = ["task", "exitCode"]
	};

	private readonly BridgeServer _bridge;
	private readonly ILogger _logger;

	private IIntegrationContext? _context;
	private string _status = BridgeStatus.NotStarted;

	// ---- Capabilities the host reads ----------------------------------------------------------------

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public IReadOnlyList<VariableDefinition> Variables { get; } =
	[
		VariableDefinition.Eager("vscode_bridge_status", VariableType.Text) with
			{ Id = "bridge-status", DisplayName = Strings.Variables.BridgeStatus() },
		VariableDefinition.Eager("vscode_connected", VariableType.Boolean) with
			{ Id = "connected", DisplayName = Strings.Variables.Connected() },
		VariableDefinition.Eager("vscode_window_count", VariableType.Numeric) with
			{ Id = "window-count", DisplayName = Strings.Variables.WindowCount() },
		VariableDefinition.Eager("vscode_target_pinned", VariableType.Boolean) with
			{ Id = "target-pinned", DisplayName = Strings.Variables.TargetPinned() },
		VariableDefinition.Eager("vscode_workspace", VariableType.Text) with
			{ Id = "workspace", DisplayName = Strings.Variables.Workspace() },
		VariableDefinition.Eager("vscode_active_file", VariableType.Text) with
			{ Id = "active-file", DisplayName = Strings.Variables.ActiveFile() },
		VariableDefinition.Eager("vscode_language", VariableType.Text) with
			{ Id = "language", DisplayName = Strings.Variables.Language() },
		VariableDefinition.Eager("vscode_git_branch", VariableType.Text) with
			{ Id = "git-branch", DisplayName = Strings.Variables.GitBranch() },
		VariableDefinition.Eager("vscode_errors", VariableType.Numeric) with
			{ Id = "errors", DisplayName = Strings.Variables.Errors() },
		VariableDefinition.Eager("vscode_warnings", VariableType.Numeric) with
			{ Id = "warnings", DisplayName = Strings.Variables.Warnings() },
		VariableDefinition.Eager("vscode_unsaved_files", VariableType.Numeric) with
			{ Id = "unsaved-files", DisplayName = Strings.Variables.UnsavedFiles() },
		VariableDefinition.Eager("vscode_debugging", VariableType.Boolean) with
			{ Id = "debugging", DisplayName = Strings.Variables.Debugging() }
	];

	public IReadOnlyList<EventDefinition> EventDefinitions { get; } =
	[
		new()
		{
			Id = "file-saved",
			Name = Strings.Events.FileSaved.Name(),
			Description = Strings.Events.FileSaved.Description(),
			PayloadParameters =
			[
				ActionParameter.Text("fileName", label: Strings.Events.FileSaved.FileName()),
				ActionParameter.Text("language", label: Strings.Events.FileSaved.Language())
			]
		},
		new()
		{
			Id = "debug-started",
			Name = Strings.Events.DebugStarted.Name(),
			Description = Strings.Events.DebugStarted.Description(),
			PayloadParameters = [ActionParameter.Text("sessionName", label: Strings.Events.SessionName())]
		},
		new()
		{
			Id = "debug-stopped",
			Name = Strings.Events.DebugStopped.Name(),
			Description = Strings.Events.DebugStopped.Description(),
			PayloadParameters = [ActionParameter.Text("sessionName", label: Strings.Events.SessionName())]
		},
		new()
		{
			Id = "task-finished",
			Name = Strings.Events.TaskFinished.Name(),
			Description = Strings.Events.TaskFinished.Description(),
			ConfigurationParameters =
			[
				ActionParameter.Text("task", label: Strings.Events.TaskFinished.Task(),
					placeholder: Strings.Events.TaskFinished.AnyTask())
			],
			PayloadParameters =
			[
				ActionParameter.Text("task", label: Strings.Events.TaskFinished.Task()),
				ActionParameter.Number("exitCode", label: Strings.Events.TaskFinished.ExitCode())
			]
		}
	];

	public bool AllowsMultipleConfigurations => false;

	public PluginIntegration(BridgeServer bridge, ILogger logger)
	{
		_bridge = bridge;
		_logger = logger.ForContext<PluginIntegration>();
		Actions = [new RunCommandAction(bridge), new RunTaskAction(bridge), new SelectWindowAction(bridge)];
	}

	// ---- Lifecycle ---------------------------------------------------------------------------------

	/// <summary>Entries saved while the Port field was a Number field hold a JSON number, and
	/// <c>GetStringAsync</c> only deserializes JSON strings, so such a value falls back to the default port
	/// instead of stopping the bridge from starting.</summary>
	private async Task<int> ReadPortAsync(IIntegrationContext context, Guid entryId)
	{
		string? text;
		try
		{
			text = await context.Config.GetStringAsync(entryId, PairingConfigFlow.PortField);
		}
		catch (System.Text.Json.JsonException ex)
		{
			_logger.Warning(ex, "The stored port is not text; using the default port {Port}", BridgeServer.DefaultPort);
			return BridgeServer.DefaultPort;
		}

		return PairingConfigFlow.TryParsePort(text, out var port) ? port : BridgeServer.DefaultPort;
	}

	private void OnBridgeEvent(VsCodeWindow window, string name, JsonObject? data)
	{
		if (!ForwardedEvents.TryGetValue(name, out var keys))
		{
			_logger.Debug("Dropped unknown event {Event} from window {WindowId}", name, window.Id);
			return;
		}

		var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
		foreach (var key in keys)
		{
			payload[key] = data?[key] switch
			{
				JsonValue v when v.TryGetValue<string>(out var s) => s,
				JsonValue v when v.TryGetValue<double>(out var d) => d,
				JsonValue v when v.TryGetValue<bool>(out var b) => b,
				null => null,
				var other => other.ToJsonString()
			};
		}

		_context?.Events.Publish(name, payload);
	}

	public async Task InitializeAsync(IIntegrationContext context)
	{
		_context = context;

		// InitializeAsync runs again after reconnects and config changes.
		_bridge.EventReceived -= OnBridgeEvent;
		_bridge.EventReceived += OnBridgeEvent;

		try
		{
			_status = BridgeStatus.ReadingConfig;
			var entries = await context.Config.GetEntriesAsync();
			var entry = entries.Count > 0 ? entries[0] : null;
			if (entry is null)
			{
				_status = BridgeStatus.NotConfigured;
				_logger.Information("Not set up yet; the bridge starts once the pairing token is entered");
				await _bridge.StopAsync();
				return;
			}

			var token = await context.Config.GetSecretAsync(entry.Id, PairingConfigFlow.TokenField);
			var port = await ReadPortAsync(context, entry.Id);

			if (string.IsNullOrWhiteSpace(token))
			{
				_status = BridgeStatus.NotConfigured;
				_logger.Warning("No pairing token is stored; waiting for setup");
				await _bridge.StopAsync();
				return;
			}

			_status = BridgeStatus.StartingListener;
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			await _bridge.StartAsync(port, token.Trim(), timeout.Token);
			_status = _bridge.PortInUse ? BridgeStatus.PortInUse : BridgeStatus.Listening;
		}
		catch (Exception ex)
		{
			_status = BridgeStatus.Error;
			_logger.Error(ex, "The VS Code bridge could not start");
			throw;
		}
	}

	public async Task ShutdownAsync()
	{
		_bridge.EventReceived -= OnBridgeEvent;
		_context = null;
		await _bridge.StopAsync();
	}

	// ---- Variables -------------------------------------------------------------------------------

	private static VariableReading TextOrUnavailable(string? value)
		=> string.IsNullOrEmpty(value) ? VariableReading.Unavailable : VariableReading.Of(value);

	/// <summary>Answers from the state the windows last pushed; never touches the socket.</summary>
	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		var window = _bridge.ActiveWindow;
		var state = window?.State;

		return ValueTask.FromResult(localId switch
		{
			"bridge-status" => VariableReading.Of(_status),
			"connected" => VariableReading.Of(window is not null),
			"window-count" => VariableReading.Of(_bridge.Windows.Count),
			"target-pinned" => VariableReading.Of(_bridge.ActiveIsPinned),
			_ when state is null => VariableReading.Unavailable,
			"workspace" => TextOrUnavailable(state.Workspace),
			"active-file" => TextOrUnavailable(state.ActiveFile),
			"language" => TextOrUnavailable(state.Language),
			"git-branch" => TextOrUnavailable(state.GitBranch),
			"errors" => VariableReading.Of(state.Errors),
			"warnings" => VariableReading.Of(state.Warnings),
			"unsaved-files" => VariableReading.Of(state.UnsavedFiles),
			"debugging" => VariableReading.Of(state.Debugging),
			_ => VariableReading.Unavailable
		});
	}

	// ---- Setup and issues ------------------------------------------------------------------------

	public IConfigFlow CreateConfigFlow() => new PairingConfigFlow();

	public Task<IReadOnlyList<IntegrationIssue>> GetIssuesAsync(CancellationToken cancellationToken = default)
	{
		IReadOnlyList<IntegrationIssue> issues = _bridge.PortInUse
			?
			[
				new IntegrationIssue
				{
					Id = PortInUseIssueId,
					Title = Strings.Issues.PortInUse.Title(),
					Description = Strings.Issues.PortInUse.Description(
						port: _bridge.Port.ToString(CultureInfo.InvariantCulture)),
					Severity = IntegrationIssueSeverity.Error,
					ActionLabel = Strings.Issues.OpenSetup()
				}
			]
			: [];

		return Task.FromResult(issues);
	}

	public Task<IssueResolution> ResolveIssueAsync(string issueId, CancellationToken cancellationToken = default)
		=> Task.FromResult(issueId == PortInUseIssueId
			? IssueResolution.Ok(followUp: IssueResolutionFollowUp.StartConfigFlow)
			: IssueResolution.Failed(Strings.Issues.Unknown()));
}
