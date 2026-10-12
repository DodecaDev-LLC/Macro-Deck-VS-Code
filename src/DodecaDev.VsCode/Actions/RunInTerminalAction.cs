using System.Text.Json;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>
/// Runs a command line in a named terminal of the active window, creating the terminal when it does not
/// exist yet, so one button can start a dev server and another can restart it.
/// </summary>
internal sealed class RunInTerminalAction(BridgeServer bridge) : IDynamicOptionsActionDefinition
{
	internal const string CommandLineParameter = "commandLine";
	internal const string TerminalParameter = "terminal";
	internal const string FocusParameter = "focus";
	internal const string InterruptParameter = "interrupt";
	internal const string DefaultTerminalName = "Macro Deck";

	public string Id => "run-in-terminal";

	public LocalizedText Name => Strings.Actions.RunInTerminal.Name();

	public LocalizedText Description => Strings.Actions.RunInTerminal.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.MultilineText(CommandLineParameter,
			label: Strings.Actions.RunInTerminal.CommandLine.Label(),
			description: Strings.Actions.RunInTerminal.CommandLine.Description(),
			placeholder: Strings.Actions.RunInTerminal.CommandLine.Placeholder(),
			required: true),
		ActionParameter.Autocomplete(TerminalParameter,
			label: Strings.Actions.RunInTerminal.Terminal.Label(),
			description: Strings.Actions.RunInTerminal.Terminal.Description(),
			placeholder: Strings.Actions.RunInTerminal.Terminal.Placeholder()),
		ActionParameter.Toggle(FocusParameter,
			label: Strings.Actions.RunInTerminal.Focus.Label(),
			defaultValue: true),
		ActionParameter.Toggle(InterruptParameter,
			label: Strings.Actions.RunInTerminal.Interrupt.Label(),
			description: Strings.Actions.RunInTerminal.Interrupt.Description())
	];

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		try
		{
			var terminals = await bridge.RequestAsync("listTerminals", null, cancellationToken);
			return BridgeOptions.Filtered(terminals?.Deserialize<List<BridgeOption>>() ?? [], context.Filter);
		}
		catch (BridgeRequestException ex)
		{
			return new DynamicOptionsResult { Options = [], Error = BridgeResults.Message(ex), AllowsCustomValue = true };
		}
	}

	public IActionExecutor CreateExecutor() => new Executor(bridge);

	private sealed class Executor(BridgeServer bridge) : IActionExecutor
	{
		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			if (context.Parameters.GetValueOrDefault(CommandLineParameter)?.ToString() is not { } commandLine
				|| string.IsNullOrWhiteSpace(commandLine))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.RunInTerminal.CommandLine.Label()));
			}

			var terminal = context.Parameters.GetValueOrDefault(TerminalParameter)?.ToString();
			try
			{
				// Answered once the text is sent, not when the command finishes: a terminal command can run
				// for hours (a dev server), and its outcome is not observable.
				await bridge.RequestAsync("runInTerminal", new JsonObject
				{
					["commandLine"] = commandLine.TrimEnd(),
					["terminal"] = string.IsNullOrWhiteSpace(terminal) ? DefaultTerminalName : terminal.Trim(),
					["focus"] = IsOn(context.Parameters.GetValueOrDefault(FocusParameter), defaultValue: true),
					["interrupt"] = IsOn(context.Parameters.GetValueOrDefault(InterruptParameter), defaultValue: false)
				}, context.CancellationToken);
				return ActionResult.Success();
			}
			catch (BridgeRequestException ex)
			{
				return BridgeResults.Failed(ex);
			}
		}
	}

	/// <summary>Toggles may arrive as a bool or as its text, depending on how the value was stored.</summary>
	internal static bool IsOn(object? value, bool defaultValue) => value switch
	{
		bool b => b,
		string s when bool.TryParse(s, out var parsed) => parsed,
		_ => defaultValue
	};
}
