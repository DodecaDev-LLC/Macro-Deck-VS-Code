using DodecaDev.VsCode.Actions;
using DodecaDev.VsCode.Bridge;
using NUnit.Framework;

namespace DodecaDev.VsCode.Tests;

[TestFixture]
public sealed class BridgeOptionsTests
{
	private static readonly BridgeOption[] Commands =
	[
		.. Enumerable.Range(0, 5000).Select(i => new BridgeOption($"ext.command{i}", null)),
		new("workbench.action.toggleZenMode", "View: Toggle Zen Mode (workbench.action.toggleZenMode)")
	];

	[Test]
	public void An_unfiltered_list_is_capped_to_stay_under_the_protocol_message_limit()
	{
		var result = BridgeOptions.Filtered(Commands, null);

		Assert.That(result.Options, Has.Count.EqualTo(BridgeOptions.MaxOptions));
		Assert.That(result.AllowsCustomValue, Is.True);
	}

	[Test]
	public void Typed_text_matches_ids_and_titles_case_insensitively()
	{
		Assert.That(BridgeOptions.Filtered(Commands, "zen mode").Options.Single().Value,
			Is.EqualTo("workbench.action.toggleZenMode"));
		Assert.That(BridgeOptions.Filtered(Commands, "TOGGLEZEN").Options, Has.Count.EqualTo(1));
	}
}
