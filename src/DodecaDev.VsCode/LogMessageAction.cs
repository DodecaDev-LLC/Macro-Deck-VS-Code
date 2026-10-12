using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace DodecaDev.VsCode;

/// <summary>
/// The one example action. Every string a user reads - the action's name and description, its
/// parameter's label, description and placeholder, and the error it can fail with - comes from
/// <see cref="Strings"/> rather than a literal, which is what makes the plugin translatable.
/// </summary>
public sealed class LogMessageAction : IActionDefinition
{
	private const string MessageParameter = "message";

	private readonly ILogger _logger;

	public LogMessageAction(ILogger logger) => _logger = logger.ForContext<LogMessageAction>();

	public string Id => "log-message";

	public LocalizedText Name => Strings.Actions.LogMessage.Name();

	public LocalizedText Description => Strings.Actions.LogMessage.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Text(
			MessageParameter,
			label: Strings.Actions.LogMessage.Message.Label(),
			description: Strings.Actions.LogMessage.Message.Description(),
			placeholder: Strings.Actions.LogMessage.Message.Placeholder(),
			required: true),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

	public IActionExecutor CreateExecutor() => new Executor(_logger);

	private sealed class Executor : IActionExecutor
	{
		private readonly ILogger _logger;

		public Executor(ILogger logger) => _logger = logger;

		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var message = context.Parameters.TryGetValue(MessageParameter, out var value)
				? value.ToString()
				: null;

			// The parameter is required, but the host still sends whatever the user configured, so the
			// executor is the only place that can decide the action did not do what it claims.
			if (string.IsNullOrWhiteSpace(message))
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.LogMessage.Message.Label())));
			}

			_logger.Information("{Message}", message);
			return ActionResult.SucceededTask;
		}
	}
}
