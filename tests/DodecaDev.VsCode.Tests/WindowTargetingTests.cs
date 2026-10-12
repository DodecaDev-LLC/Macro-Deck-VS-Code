using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Plugin.Protocol.Capabilities.Actions;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace DodecaDev.VsCode.Tests;

/// <summary>
/// Which window a button controls: pinned beats focused, a button's own Window setting beats both, and
/// "all windows" reaches every one.
/// </summary>
[TestFixture]
public sealed class WindowTargetingTests
{
	private const string Token = "test-pairing-token-0123456789";
	private const string AlphaKey = "vscode-remote://wsl+ubuntu/home/me/alpha";
	private const string BetaKey = "file:///c%3A/src/beta";

	private PluginTestHarness _harness = null!;
	private int _port;

	[SetUp]
	public async Task SetUpAsync()
	{
		_port = FakeVsCodeWindow.FreePort();
		_harness = PluginTestHarness.Create(builder =>
		{
			builder.UseLocalization(Strings.LocalizationCatalog).RegisterIntegration<PluginIntegration>();
			builder.Services.AddSingleton<BridgeServer>();
		});
		var entryId = _harness.Context.Config.AddEntry("VS Code");
		_harness.Context.Config.SeedSecret(entryId, "pairing_token", Token);
		_harness.Context.Config.SeedString(entryId, "port", _port.ToString(CultureInfo.InvariantCulture));
		await _harness.InitializeIntegrationsAsync();
	}

	[TearDown]
	public async Task TearDownAsync() => await _harness.DisposeAsync();

	/// <summary>Connects a window and waits until the plugin has applied its state report.</summary>
	private async Task<FakeVsCodeWindow> OpenAsync(string windowId, string key, string name, bool focused)
	{
		var (window, welcomed) = await FakeVsCodeWindow.ConnectAsync(_port, Token, windowId);
		Assert.That(welcomed, Is.True);
		await window.SendStateAsync(new JsonObject { ["focused"] = focused, ["workspace"] = name, ["workspaceKey"] = key });

		// Until its first state report is applied the plugin cannot know the window's workspace, so wait for
		// the picker (answered from memory) to list it. Real time: the bridge runs on real sockets.
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (!(await WindowValuesAsync()).Contains(key))
		{
			Assert.That(DateTime.UtcNow, Is.LessThan(deadline), $"window {windowId} should report its workspace");
			await Task.Delay(10);
		}

		return window;
	}

	private async Task<List<string?>> WindowValuesAsync()
		=> [.. (await _harness.Actions.GetOptionsAsync("run-command", "window")).Data!.Value.GetProperty("options")
			.EnumerateArray().Select(o => o.GetProperty("value").GetString())];

	/// <summary>A request to this window, answered by it: every message it sent before is now applied.</summary>
	private async Task SyncAsync(FakeVsCodeWindow window, string key)
	{
		var execution = Run("noop", key);
		await window.AnswerNextRequestAsync();
		Assert.That((await execution).Succeeded, Is.True);
	}

	private Task<CapabilityInvocationOutcome> Run(string command, string? window = null)
		=> _harness.Actions.ExecuteAsync("run-command",
			new Dictionary<string, object?> { ["command"] = command, ["window"] = window });

	private async Task<string?> SelectButtonStateAsync()
		=> (await _harness.Actions.GetActionStateAsync("select-window")).Data!.Value.GetProperty("activeStateId").GetString();

	[Test]
	public async Task A_pinned_window_gets_actions_even_while_another_is_focused()
	{
		await using var alpha = await OpenAsync("a", AlphaKey, "alpha", focused: false);
		await using var beta = await OpenAsync("b", BetaKey, "beta", focused: true);

		await alpha.SendPinAsync(true);
		await SyncAsync(alpha, AlphaKey);
		var execution = Run("editor.action.formatDocument");
		var request = await alpha.AnswerNextRequestAsync();

		Assert.That((await execution).Succeeded, Is.True);
		Assert.That((string?)request["params"]!["command"], Is.EqualTo("editor.action.formatDocument"));
		Assert.That(await SelectButtonStateAsync(), Is.EqualTo("pinned"));
		Assert.That(alpha.TargetNotices.Last()["windowId"]!.GetValue<string>(), Is.EqualTo("a"));
		Assert.That(alpha.TargetNotices.Last()["pinned"]!.GetValue<bool>(), Is.True);
	}

	[Test]
	public async Task A_pin_survives_reloading_the_pinned_window()
	{
		await using var beta = await OpenAsync("b", BetaKey, "beta", focused: true);
		await using (var alpha = await OpenAsync("a", AlphaKey, "alpha", focused: false))
		{
			await alpha.SendPinAsync(true);
			await SyncAsync(alpha, AlphaKey);
		}

		// The reloaded window has a new id but the same workspace.
		await using var reloaded = await OpenAsync("a2", AlphaKey, "alpha", focused: false);
		var execution = Run("workbench.action.files.save");
		await reloaded.AnswerNextRequestAsync();

		Assert.That((await execution).Succeeded, Is.True);
		Assert.That((await _harness.Variables.GetAsync("target-pinned")).DataAs<VariableReadingDto>()!.Value.Boolean, Is.True);
	}

	[Test]
	public async Task A_button_with_a_workspace_targets_it_by_key_or_by_typed_name()
	{
		await using var alpha = await OpenAsync("a", AlphaKey, "alpha", focused: true);
		await using var beta = await OpenAsync("b", BetaKey, "beta", focused: false);

		var byKey = Run("one", BetaKey);
		Assert.That((string?)(await beta.AnswerNextRequestAsync())["params"]!["command"], Is.EqualTo("one"));
		var byName = Run("two", "BETA");
		Assert.That((string?)(await beta.AnswerNextRequestAsync())["params"]!["command"], Is.EqualTo("two"));

		Assert.That((await byKey).Succeeded && (await byName).Succeeded, Is.True);
	}

	[Test]
	public async Task A_button_for_a_workspace_that_is_not_open_fails()
	{
		await using var alpha = await OpenAsync("a", AlphaKey, "alpha", focused: true);

		var outcome = await Run("anything", "vscode-remote://wsl+ubuntu/home/me/closed-project");

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task All_windows_sends_to_every_window()
	{
		await using var alpha = await OpenAsync("a", AlphaKey, "alpha", focused: true);
		await using var beta = await OpenAsync("b", BetaKey, "beta", focused: false);

		var execution = Run("workbench.action.files.saveAll", "all");
		var toAlpha = await alpha.AnswerNextRequestAsync();
		var toBeta = await beta.AnswerNextRequestAsync();

		Assert.That((await execution).Succeeded, Is.True);
		Assert.That((string?)toAlpha["params"]!["command"], Is.EqualTo("workbench.action.files.saveAll"));
		Assert.That((string?)toBeta["params"]!["command"], Is.EqualTo("workbench.action.files.saveAll"));
	}

	[Test]
	public async Task Next_window_cycles_and_toggle_goes_back_to_following_focus()
	{
		await using var alpha = await OpenAsync("a", AlphaKey, "alpha", focused: true);
		await using var beta = await OpenAsync("b", BetaKey, "beta", focused: false);
		Assert.That(await SelectButtonStateAsync(), Is.EqualTo("following"));

		// Active is alpha (focused); the next one in name order is beta.
		await _harness.Actions.ExecuteAsync("select-window", new Dictionary<string, object?> { ["mode"] = "next" });
		var toBeta = Run("after-next");
		await beta.AnswerNextRequestAsync();
		Assert.That((await toBeta).Succeeded, Is.True);
		Assert.That(await SelectButtonStateAsync(), Is.EqualTo("pinned"));

		await _harness.Actions.ExecuteAsync("select-window", new Dictionary<string, object?> { ["mode"] = "toggle" });
		var toAlpha = Run("after-unpin");
		await alpha.AnswerNextRequestAsync();
		Assert.That((await toAlpha).Succeeded, Is.True);
		Assert.That(await SelectButtonStateAsync(), Is.EqualTo("following"));
	}

	[Test]
	public async Task The_window_picker_offers_active_all_and_each_open_workspace()
	{
		await using var alpha = await OpenAsync("a", AlphaKey, "alpha", focused: true);
		await using var beta = await OpenAsync("b", BetaKey, "beta", focused: false);

		Assert.That(await WindowValuesAsync(), Is.EqualTo(new[] { "active", "all", AlphaKey, BetaKey }));
	}

	[Test]
	public void Friendly_names_come_from_the_last_path_segment()
	{
		Assert.That(BridgeServer.FriendlyName("vscode-remote://wsl+ubuntu/home/me/alpha/"), Is.EqualTo("alpha"));
		Assert.That(BridgeServer.FriendlyName("file:///c%3A/src/My%20App.code-workspace"), Is.EqualTo("My App"));
		Assert.That(BridgeServer.FriendlyName("beta"), Is.EqualTo("beta"));
	}
}
