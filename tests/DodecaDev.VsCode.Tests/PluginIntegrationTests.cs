using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Plugin.Protocol.Capabilities.ConfigFlow;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace DodecaDev.VsCode.Tests;

/// <summary>
/// Behaviour tests through <see cref="PluginTestHarness"/>, with the bridge listening on a real loopback
/// port and <see cref="FakeVsCodeWindow"/> playing the companion extension.
/// </summary>
[TestFixture]
public sealed class PluginIntegrationTests
{
	private const string Token = "test-pairing-token-0123456789";

	private static PluginTestHarness CreateHarness() =>
		PluginTestHarness.Create(builder =>
		{
			builder
				.UseLocalization(Strings.LocalizationCatalog)
				.RegisterIntegration<PluginIntegration>();
			builder.Services.AddSingleton<BridgeServer>();
		});

	private static async Task<(PluginTestHarness Harness, int Port)> PairedHarnessAsync()
	{
		var port = FakeVsCodeWindow.FreePort();
		var harness = CreateHarness();
		var entryId = harness.Context.Config.AddEntry("VS Code");
		harness.Context.Config.SeedSecret(entryId, "pairing_token", Token);
		harness.Context.Config.SeedString(entryId, "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
		await harness.InitializeIntegrationsAsync();
		return (harness, port);
	}

	private static ConfigFlowOAuthContextDto OAuth() => new() { RedirectUri = "http://localhost/callback", State = "state-1" };

	private static async Task<VariableReadingDto> ReadAsync(PluginTestHarness harness, string id)
		=> (await harness.Variables.GetAsync(id)).DataAs<VariableReadingDto>()!;

	/// <summary>A window's messages are handled in order, so once a request it answers after a state report
	/// completes, that report has been applied.</summary>
	private static async Task RoundTripAsync(PluginTestHarness harness, FakeVsCodeWindow window)
	{
		var execution = harness.Actions.ExecuteAsync("run-command",
			new Dictionary<string, object?> { ["command"] = "noop" });
		await window.AnswerNextRequestAsync();
		await execution;
	}

	[Test]
	public async Task Without_a_window_commands_fail_as_not_connected()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync("run-command",
			new Dictionary<string, object?> { ["command"] = "workbench.action.files.save" });

		Assert.That(outcome.Succeeded, Is.False);
		Assert.That((await ReadAsync(harness, "connected")).Value.Boolean, Is.False);
	}

	[Test]
	public async Task The_bridge_status_says_whether_setup_is_missing_or_the_bridge_is_listening()
	{
		await using var unconfigured = CreateHarness();
		await unconfigured.InitializeIntegrationsAsync();
		var (paired, _) = await PairedHarnessAsync();
		await using var _ = paired;

		Assert.That((await ReadAsync(unconfigured, "bridge-status")).Value.Text, Is.EqualTo("not-configured"));
		Assert.That((await ReadAsync(paired, "bridge-status")).Value.Text, Is.EqualTo("listening"));
	}

	[Test]
	public async Task A_paired_window_reports_its_state_as_variables()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, welcomed) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		await window.SendStateAsync(new JsonObject
		{
			["focused"] = true, ["workspace"] = "MacroDeck3", ["gitBranch"] = "main", ["errors"] = 3
		});
		await RoundTripAsync(harness, window);

		Assert.That(welcomed, Is.True);
		Assert.That((await ReadAsync(harness, "connected")).Value.Boolean, Is.True);
		Assert.That((await ReadAsync(harness, "git-branch")).Value.Text, Is.EqualTo("main"));
		Assert.That((await ReadAsync(harness, "workspace")).Value.Text, Is.EqualTo("MacroDeck3"));
	}

	[Test]
	public async Task Run_command_sends_the_command_and_spreads_a_json_array_of_arguments()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		var execution = harness.Actions.ExecuteAsync("run-command", new Dictionary<string, object?>
		{
			["command"] = "workbench.action.tasks.runTask",
			["arguments"] = "[\"build\"]"
		});
		var request = await window.AnswerNextRequestAsync();
		var outcome = await execution;

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That((string?)request["method"], Is.EqualTo("executeCommand"));
		Assert.That((string?)request["params"]!["command"], Is.EqualTo("workbench.action.tasks.runTask"));
		Assert.That(request["params"]!["args"]!.ToJsonString(), Is.EqualTo("[\"build\"]"));
	}

	private static async Task<string?> DebugButtonStateAsync(PluginTestHarness harness)
		=> (await harness.Actions.GetActionStateAsync("debug-control")).Data!.Value.GetProperty("activeStateId").GetString();

	[Test]
	public async Task The_debug_button_follows_the_window_from_unavailable_to_paused()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;

		Assert.That(await DebugButtonStateAsync(harness), Is.EqualTo("unavailable"));

		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;
		await window.SendStateAsync(new JsonObject { ["focused"] = true, ["debugging"] = true, ["debugState"] = "paused" });
		await RoundTripAsync(harness, window);

		Assert.That(await DebugButtonStateAsync(harness), Is.EqualTo("paused"));
		Assert.That((await ReadAsync(harness, "debug-state")).Value.Text, Is.EqualTo("paused"));
	}

	[Test]
	public async Task An_older_extension_without_debug_state_still_shows_running()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		await window.SendStateAsync(new JsonObject { ["focused"] = true, ["debugging"] = true });
		await RoundTripAsync(harness, window);

		Assert.That(await DebugButtonStateAsync(harness), Is.EqualTo("running"));
	}

	[Test]
	public async Task Debug_control_sends_the_command_and_configuration()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		var execution = harness.Actions.ExecuteAsync("debug-control", new Dictionary<string, object?>
		{
			["command"] = "start",
			["configuration"] = "Launch API"
		});
		var request = await window.AnswerNextRequestAsync();

		Assert.That((await execution).Succeeded, Is.True);
		Assert.That((string?)request["method"], Is.EqualTo("debug"));
		Assert.That((string?)request["params"]!["command"], Is.EqualTo("start"));
		Assert.That((string?)request["params"]!["configuration"], Is.EqualTo("Launch API"));
	}

	[Test]
	public async Task Debug_control_refuses_an_unknown_command_without_contacting_VS_Code()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		var outcome = await harness.Actions.ExecuteAsync("debug-control",
			new Dictionary<string, object?> { ["command"] = "launch-rockets" });

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task Run_in_terminal_defaults_to_the_Macro_Deck_terminal_and_focuses_it()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		var execution = harness.Actions.ExecuteAsync("run-in-terminal",
			new Dictionary<string, object?> { ["commandLine"] = "npm run dev\n", ["interrupt"] = "true" });
		var request = await window.AnswerNextRequestAsync();

		Assert.That((await execution).Succeeded, Is.True);
		Assert.That((string?)request["method"], Is.EqualTo("runInTerminal"));
		Assert.That((string?)request["params"]!["commandLine"], Is.EqualTo("npm run dev"));
		Assert.That((string?)request["params"]!["terminal"], Is.EqualTo("Macro Deck"));
		Assert.That((bool?)request["params"]!["focus"], Is.True);
		Assert.That((bool?)request["params"]!["interrupt"], Is.True);
	}

	[Test]
	public async Task Run_in_terminal_refuses_a_blank_command_line()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		var outcome = await harness.Actions.ExecuteAsync("run-in-terminal",
			new Dictionary<string, object?> { ["commandLine"] = "  \n " });

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task A_command_VS_Code_rejects_fails_the_action()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		var execution = harness.Actions.ExecuteAsync("run-command",
			new Dictionary<string, object?> { ["command"] = "no.such.command" });
		await window.AnswerNextRequestAsync(error: "command 'no.such.command' not found");

		Assert.That((await execution).Succeeded, Is.False);
	}

	[Test]
	public async Task Invalid_json_arguments_fail_before_anything_is_sent()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		var outcome = await harness.Actions.ExecuteAsync("run-command", new Dictionary<string, object?>
		{
			["command"] = "editor.action.formatDocument",
			["arguments"] = "{not json"
		});

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task A_window_with_the_wrong_token_is_refused()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;

		var (window, welcomed) = await FakeVsCodeWindow.ConnectAsync(port, "some-other-token-0123456789");
		await using var __ = window;

		// The plugin proves itself first, so the window already knows the token is wrong.
		Assert.That(welcomed, Is.False);
		Assert.That((await ReadAsync(harness, "connected")).Value.Boolean, Is.False);
	}

	[Test]
	public async Task A_browser_connection_is_refused()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;

		Assert.ThrowsAsync<WebSocketException>(() => FakeVsCodeWindow.ConnectAsync(port, Token,
			configure: o => o.SetRequestHeader("Origin", "https://example.com")));
	}

	[Test]
	public async Task Window_events_are_published_with_their_declared_payload()
	{
		var (harness, port) = await PairedHarnessAsync();
		await using var _ = harness;
		var (window, _) = await FakeVsCodeWindow.ConnectAsync(port, Token);
		await using var __ = window;

		await window.SendEventAsync("task-finished", new JsonObject { ["task"] = "build", ["exitCode"] = 1, ["extra"] = "dropped" });
		await window.SendEventAsync("not-declared", new JsonObject());
		await Wait.UntilAsync(() => harness.Context.Events.Published.Count > 0, because: "the event should be forwarded");

		var published = harness.Context.Events.Published.Single();
		var payload = published.Parameters!.Value;
		Assert.That(published.EventId, Is.EqualTo("task-finished"));
		Assert.That(payload.GetProperty("task").GetString(), Is.EqualTo("build"));
		Assert.That(payload.GetProperty("exitCode").GetDouble(), Is.EqualTo(1));
		Assert.That(payload.TryGetProperty("extra", out JsonElement _), Is.False);
	}

	[Test]
	public async Task Setup_refuses_a_port_out_of_range()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();

		await harness.ConfigFlow.StartAsync(new FlowStartArguments { SessionId = "setup", OAuth = OAuth() });
		var result = (await harness.ConfigFlow.SubmitAsync(new FlowSubmitArguments
		{
			SessionId = "setup",
			StepId = "pairing",
			OAuth = OAuth(),
			Input = new Dictionary<string, JsonElement>
			{
				["pairing_token"] = JsonSerializer.SerializeToElement(Token),
				["port"] = JsonSerializer.SerializeToElement(80)
			}
		})).DataAs<ConfigFlowResultDto>();

		Assert.That(result!.Kind, Is.EqualTo("Error"));
		Assert.That(result.FieldErrors, Does.ContainKey("port"));
	}
}

/// <summary>
/// The localization set is generated from <c>Localization/*.resx</c>, so these guard the wiring rather
/// than any wording: a missing catalog registration leaves every label showing its raw key.
/// </summary>
[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.dodecadev.vscode"));
	}

	[Test]
	public void English_is_the_default_culture()
	{
		Assert.That(Strings.LocalizationCatalog.DefaultCulture, Is.EqualTo("en"));
		Assert.That(Strings.LocalizationCatalog.Cultures, Does.Contain("en"));
	}

	[Test]
	public void The_action_strings_come_from_the_catalog()
	{
		Assert.That(Strings.LocalizationCatalog.KeysOf("en"), Does.Contain("Actions.RunCommand.Name"));
	}

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
			Assert.That(text, Is.Not.Empty);
		}
	}
}
