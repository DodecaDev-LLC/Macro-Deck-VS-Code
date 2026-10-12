using System.Text.Json;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>Runs a task from the active window's workspace (tasks.json, npm scripts, build tasks...).</summary>
internal sealed class RunTaskAction(BridgeServer bridge) : IDynamicOptionsActionDefinition
{
	internal const string TaskParameter = "task";

	public string Id => "run-task";

	public LocalizedText Name => Strings.Actions.RunTask.Name();

	public LocalizedText Description => Strings.Actions.RunTask.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Autocomplete(TaskParameter,
			label: Strings.Actions.RunTask.Task.Label(),
			description: Strings.Actions.RunTask.Task.Description(),
			required: true),
		WindowTarget.Parameter()
	];

	public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		if (context.ParameterName == WindowTarget.ParameterName)
		{
			return WindowTarget.Options(bridge, context.Filter);
		}

		// The list comes from the window this button targets; with "all windows", from the active one.
		var target = WindowTarget.Read(context.CurrentParameters);
		target = target == BridgeServer.TargetAll ? null : target;
		try
		{
			var tasks = await bridge.RequestAsync("listTasks", null, cancellationToken, target);
			return BridgeOptions.Filtered(tasks?.Deserialize<List<BridgeOption>>() ?? [], context.Filter);
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
			if (context.Parameters.GetValueOrDefault(TaskParameter)?.ToString() is not { Length: > 0 } task)
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.RunTask.Task.Label()));
			}

			try
			{
				// The window answers once the task has started, not when it ends: the task-finished event
				// reports the outcome.
				await bridge.RequestAsync("runTask", new JsonObject { ["task"] = task }, context.CancellationToken,
					WindowTarget.Read(context.Parameters));
				return ActionResult.Success();
			}
			catch (BridgeRequestException ex)
			{
				return BridgeResults.Failed(ex);
			}
		}
	}
}
