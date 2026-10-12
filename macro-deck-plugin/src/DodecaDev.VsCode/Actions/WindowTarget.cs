using DodecaDev.VsCode.Bridge;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>
/// The optional "Window" field every VS Code action carries: the active window (pinned, else last
/// focused), every window, or one workspace by name, so a button can be bound to a project.
/// </summary>
internal static class WindowTarget
{
	internal const string ParameterName = "window";

	internal static ActionParameter Parameter(bool allowAll = true) => ActionParameter.Autocomplete(ParameterName,
		label: Strings.Actions.Window.Label(),
		description: allowAll ? Strings.Actions.Window.Description() : Strings.Actions.Window.DescriptionSingle(),
		placeholder: Strings.Actions.Window.Placeholder());

	/// <summary>The choices: active, optionally all, then each open workspace. Answered from the windows the
	/// bridge already knows, so it never waits on VS Code.</summary>
	internal static DynamicOptionsResult Options(BridgeServer bridge, string? filter, bool allowAll = true)
	{
		var text = filter?.Trim() ?? "";
		bool Matches(string value, string? name) => text.Length == 0
			|| value.Contains(text, StringComparison.OrdinalIgnoreCase)
			|| (name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

		List<ActionParameterOption> options = [];
		if (Matches(BridgeServer.TargetActive, null))
		{
			options.Add(new ActionParameterOption { Value = BridgeServer.TargetActive, Label = Strings.Actions.Window.Active() });
		}

		if (allowAll && Matches(BridgeServer.TargetAll, null))
		{
			options.Add(new ActionParameterOption { Value = BridgeServer.TargetAll, Label = Strings.Actions.Window.All() });
		}

		foreach (var window in bridge.OrderedWindows.DistinctBy(w => w.TargetKey))
		{
			if (Matches(window.TargetKey, window.State.Workspace))
			{
				options.Add(new ActionParameterOption
				{
					Value = window.TargetKey,
					Label = window.State.Workspace is { Length: > 0 } name ? name : Strings.Actions.Window.NoFolder()
				});
			}
		}

		return new DynamicOptionsResult { Options = options, AllowsCustomValue = true };
	}

	/// <summary>Generic so it takes both execution parameters and an options draft, whose dictionaries
	/// differ only in nullability.</summary>
	internal static string? Read<TValue>(IReadOnlyDictionary<string, TValue> parameters)
		=> parameters.GetValueOrDefault(ParameterName)?.ToString();
}
