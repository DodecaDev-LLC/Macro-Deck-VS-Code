using DodecaDev.VsCode.Bridge;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>
/// Narrows a window's command or task list to what the user typed. VS Code has thousands of commands,
/// and the whole list is larger than one plugin protocol message may be (256 KB), which the host
/// refuses as a malformed envelope.
/// </summary>
internal static class BridgeOptions
{
	internal const int MaxOptions = 200;

	internal static DynamicOptionsResult Filtered(IEnumerable<BridgeOption> options, string? filter)
	{
		var text = filter?.Trim() ?? "";
		var matches = options.Where(o => text.Length == 0
			|| o.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
			|| (o.Label?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));

		return new DynamicOptionsResult
		{
			Options = [.. matches.Take(MaxOptions).Select(o => new ActionParameterOption { Value = o.Id, Label = o.Label ?? o.Id })],
			AllowsCustomValue = true
		};
	}
}
