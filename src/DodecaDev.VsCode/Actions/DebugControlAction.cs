using System.Text.Json;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>
/// Starts, stops and steps the debugger in the active window, and shows on the button whether a debug
/// session is stopped, running or paused at a breakpoint.
/// </summary>
internal sealed class DebugControlAction(BridgeServer bridge) : IDynamicOptionsActionDefinition, IStateProviderActionDefinition
{
	internal const string CommandParameter = "command";
	internal const string ConfigurationParameter = "configuration";

	// Command and state ids are persisted in users' profiles: never rename them.
	internal const string StoppedState = "stopped";
	internal const string RunningState = "running";
	internal const string PausedState = "paused";
	internal const string UnavailableState = "unavailable";

	private static readonly string[] Commands =
		["toggle", "start", "stop", "restart", "continue", "pause", "step-over", "step-into", "step-out"];

	private static readonly IReadOnlyList<ActionStateDefinition> States =
	[
		new(StoppedState, MacroDeckStrings.States.Stopped()),
		new(RunningState, Strings.Actions.DebugControl.States.Running())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2f855a" } },
		new(PausedState, MacroDeckStrings.States.Paused())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#b7791f" } },
		new(UnavailableState, MacroDeckStrings.States.Unavailable())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#4a5568" } }
	];

	public string Id => "debug-control";

	public LocalizedText Name => Strings.Actions.DebugControl.Name();

	public LocalizedText Description => Strings.Actions.DebugControl.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(CommandParameter,
			[
				Option("toggle", Strings.Actions.DebugControl.Commands.Toggle()),
				Option("start", Strings.Actions.DebugControl.Commands.Start()),
				Option("stop", Strings.Actions.DebugControl.Commands.Stop()),
				Option("restart", Strings.Actions.DebugControl.Commands.Restart()),
				Option("continue", Strings.Actions.DebugControl.Commands.Continue()),
				Option("pause", Strings.Actions.DebugControl.Commands.Pause()),
				Option("step-over", Strings.Actions.DebugControl.Commands.StepOver()),
				Option("step-into", Strings.Actions.DebugControl.Commands.StepInto()),
				Option("step-out", Strings.Actions.DebugControl.Commands.StepOut())
			],
			label: Strings.Actions.DebugControl.Command.Label(),
			defaultValue: "toggle",
			required: true),
		ActionParameter.Autocomplete(ConfigurationParameter,
			label: Strings.Actions.DebugControl.Configuration.Label(),
			description: Strings.Actions.DebugControl.Configuration.Description(),
			placeholder: Strings.Actions.DebugControl.Configuration.Placeholder())
			.OnlyWhen(CommandParameter, "toggle", "start")
	];

	public TimeSpan StatePollInterval => TimeSpan.FromSeconds(1);

	/// <summary>Answers from the state the window last pushed; never touches the socket.</summary>
	public Task<ActionStateSnapshot?> GetActionStateAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
	{
		var active = bridge.ActiveWindow?.State switch
		{
			null => UnavailableState,
			{ DebugState: RunningState or PausedState or StoppedState } state => state.DebugState,
			{ Debugging: true } => RunningState,
			_ => StoppedState
		};

		return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(States, active));
	}

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		try
		{
			var configurations = await bridge.RequestAsync("listLaunchConfigs", null, cancellationToken);
			return BridgeOptions.Filtered(configurations?.Deserialize<List<BridgeOption>>() ?? [], context.Filter);
		}
		catch (BridgeRequestException ex)
		{
			return new DynamicOptionsResult { Options = [], Error = BridgeResults.Message(ex), AllowsCustomValue = true };
		}
	}

	public IActionExecutor CreateExecutor() => new Executor(bridge);

	private static ActionParameterOption Option(string value, LocalizedText label)
		=> new() { Value = value, Label = label };

	private sealed class Executor(BridgeServer bridge) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var command = context.Parameters.GetValueOrDefault(CommandParameter)?.ToString() ?? "toggle";
			if (!Commands.Contains(command))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.DebugControl.Command.Label()));
			}

			var configuration = context.Parameters.GetValueOrDefault(ConfigurationParameter)?.ToString();
			try
			{
				await bridge.RequestAsync("debug", new JsonObject
				{
					["command"] = command,
					["configuration"] = string.IsNullOrWhiteSpace(configuration) ? null : configuration.Trim()
				}, context.CancellationToken);
				return ActionResult.Success();
			}
			catch (BridgeRequestException ex)
			{
				return BridgeResults.Failed(ex);
			}
		}
	}
}
