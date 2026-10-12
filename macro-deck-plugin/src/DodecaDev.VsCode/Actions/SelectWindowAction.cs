using DodecaDev.VsCode.Bridge;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>
/// Chooses which VS Code window the other actions control by default: pin the window in use, cycle to the
/// next one, pin a particular workspace, or go back to following focus. The button shows whether a window
/// is pinned.
/// </summary>
internal sealed class SelectWindowAction(BridgeServer bridge) : IDynamicOptionsActionDefinition, IStateProviderActionDefinition
{
	internal const string ModeParameter = "mode";

	// Mode and state ids are persisted in users' profiles: never rename them.
	internal const string ToggleMode = "toggle";
	internal const string NextMode = "next";
	internal const string PinMode = "pin";
	internal const string UnpinMode = "unpin";

	internal const string PinnedState = "pinned";
	internal const string FollowingState = "following";
	internal const string UnavailableState = "unavailable";

	private static readonly IReadOnlyList<ActionStateDefinition> States =
	[
		new(PinnedState, Strings.Actions.SelectWindow.States.Pinned())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#2b6cb0" } },
		new(FollowingState, Strings.Actions.SelectWindow.States.Following()),
		new(UnavailableState, MacroDeckStrings.States.Unavailable())
			{ DefaultAppearance = new ActionStateAppearance { BackgroundColor = "#4a5568" } }
	];

	public string Id => "select-window";

	public LocalizedText Name => Strings.Actions.SelectWindow.Name();

	public LocalizedText Description => Strings.Actions.SelectWindow.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.Choice(ModeParameter,
			[
				new ActionParameterOption { Value = ToggleMode, Label = Strings.Actions.SelectWindow.Modes.Toggle() },
				new ActionParameterOption { Value = NextMode, Label = Strings.Actions.SelectWindow.Modes.Next() },
				new ActionParameterOption { Value = PinMode, Label = Strings.Actions.SelectWindow.Modes.Pin() },
				new ActionParameterOption { Value = UnpinMode, Label = Strings.Actions.SelectWindow.Modes.Unpin() }
			],
			label: Strings.Actions.SelectWindow.Mode.Label(),
			defaultValue: ToggleMode,
			required: true),
		WindowTarget.Parameter(allowAll: false).OnlyWhen(ModeParameter, PinMode)
	];

	public Task<ActionStateSnapshot?> GetActionStateAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
	{
		var active = bridge.ActiveWindow is null ? UnavailableState
			: bridge.ActiveIsPinned ? PinnedState
			: FollowingState;
		return Task.FromResult<ActionStateSnapshot?>(new ActionStateSnapshot(States, active));
	}

	public Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
		=> Task.FromResult(WindowTarget.Options(bridge, context.Filter, allowAll: false));

	public IActionExecutor CreateExecutor() => new Executor(bridge);

	private sealed class Executor(BridgeServer bridge) : IActionExecutor
	{
		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			var mode = context.Parameters.GetValueOrDefault(ModeParameter)?.ToString() is { Length: > 0 } m ? m : ToggleMode;
			return Task.FromResult(mode switch
			{
				ToggleMode when bridge.ActiveIsPinned => Unpin(),
				ToggleMode => PinActive(),
				NextMode => bridge.PinNext() is null ? NoWindow() : ActionResult.Success(PinnedState),
				PinMode => PinChosen(WindowTarget.Read(context.Parameters)),
				UnpinMode => Unpin(),
				_ => ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.SelectWindow.Mode.Label()))
			});
		}

		private ActionResult PinActive()
		{
			if (bridge.ActiveWindow is not { } window)
			{
				return NoWindow();
			}

			bridge.Pin(window.TargetKey);
			return ActionResult.Success(PinnedState);
		}

		private ActionResult PinChosen(string? target)
		{
			if (string.IsNullOrWhiteSpace(target) || target == BridgeServer.TargetActive)
			{
				return PinActive();
			}

			// A key from the picker pins that workspace even while it is closed; it becomes the target as
			// soon as it opens. A typed name has to match an open window to learn its key.
			if (bridge.ResolveTargets(target) is [var window])
			{
				bridge.Pin(window.TargetKey);
			}
			else if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith("window:", StringComparison.Ordinal))
			{
				bridge.Pin(target.Trim());
			}
			else
			{
				return ActionResult.Failed(ActionErrorCodes.NotFound,
					Strings.Errors.WorkspaceNotOpen(workspace: BridgeServer.FriendlyName(target)));
			}

			return ActionResult.Success(PinnedState);
		}

		private ActionResult Unpin()
		{
			bridge.Pin(null);
			return ActionResult.Success(bridge.ActiveWindow is null ? UnavailableState : FollowingState);
		}

		private static ActionResult NoWindow() => ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.NoWindow());
	}
}
