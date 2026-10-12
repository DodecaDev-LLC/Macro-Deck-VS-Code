using System.Text.Json;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>
/// Runs any VS Code command by id in the active window: the one action that reaches nearly everything
/// VS Code and its extensions can do (formatting, layout, Git, debugging, Copilot, ...).
/// </summary>
internal sealed class RunCommandAction(BridgeServer bridge) : IDynamicOptionsActionDefinition
{
	internal const string CommandParameter = "command";
	internal const string ArgumentsParameter = "arguments";

	public string Id => "run-command";

	public LocalizedText Name => Strings.Actions.RunCommand.Name();

	public LocalizedText Description => Strings.Actions.RunCommand.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Autocomplete(CommandParameter,
			label: Strings.Actions.RunCommand.Command.Label(),
			description: Strings.Actions.RunCommand.Command.Description(),
			required: true),
		ActionParameter.MultilineText(ArgumentsParameter,
			label: Strings.Actions.RunCommand.Arguments.Label(),
			description: Strings.Actions.RunCommand.Arguments.Description(),
			placeholder: Strings.Actions.RunCommand.Arguments.Placeholder())
	];

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		try
		{
			var commands = await bridge.RequestAsync("listCommands", null, cancellationToken);
			return BridgeOptions.Filtered(commands?.Deserialize<List<BridgeOption>>() ?? [], context.Filter);
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
			if (context.Parameters.GetValueOrDefault(CommandParameter)?.ToString() is not { Length: > 0 } command)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.RunCommand.Command.Label()));
			}

			if (!TryParseArguments(context.Parameters.GetValueOrDefault(ArgumentsParameter)?.ToString(), out var arguments))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter, Strings.Errors.InvalidArguments());
			}

			try
			{
				await bridge.RequestAsync("executeCommand",
					new JsonObject { ["command"] = command.Trim(), ["args"] = arguments },
					context.CancellationToken);
				return ActionResult.Success();
			}
			catch (BridgeRequestException ex)
			{
				return BridgeResults.Failed(ex);
			}
		}
	}

	/// <summary>Blank means no arguments, a JSON array is spread over the command's parameters, and any
	/// other JSON value is passed as its single argument.</summary>
	internal static bool TryParseArguments(string? text, out JsonArray arguments)
	{
		arguments = [];
		if (string.IsNullOrWhiteSpace(text))
		{
			return true;
		}

		try
		{
			var node = JsonNode.Parse(text);
			arguments = node as JsonArray ?? [node];
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}
}
