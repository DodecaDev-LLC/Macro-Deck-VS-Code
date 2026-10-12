using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace DodecaDev.VsCode.Tests;

[TestFixture]
public sealed class BridgeAuthTests
{
	/// <summary>The same vector is pinned in <c>vscode-extension/test/bridge.test.js</c> at the repository root.</summary>
	[Test]
	public void The_proof_matches_the_vector_the_extension_tests_pin()
	{
		Assert.That(BridgeAuth.Proof("test-pairing-token-0123456789", BridgeAuth.ServerRole, "nonce-0123456789abcdef"),
			Is.EqualTo("37e088aa698f8dcbaf966d769cade228a12ff3c66a14428eeb14565d4d58d2de"));
	}
}

/// <summary>
/// Runs the companion extension's real <c>BridgeClient</c> (TypeScript, in Node) against the plugin's real
/// <see cref="BridgeServer"/>. Explicit because it needs Node and <c>npm test</c> run once in
/// <c>vscode-extension/</c>: <c>dotnet test --filter TestCategory=Interop</c>.
/// </summary>
[TestFixture, Explicit("Needs Node and a built vscode-extension/out"), Category("Interop")]
public sealed class BridgeInteropTests
{
	private const string Token = "interop-pairing-token-0123456789";

	[Test]
	public async Task The_typescript_client_pairs_reports_state_and_runs_a_command()
	{
		var port = FakeVsCodeWindow.FreePort();
		await using var harness = PluginTestHarness.Create(builder =>
		{
			builder.UseLocalization(Strings.LocalizationCatalog).RegisterIntegration<PluginIntegration>();
			builder.Services.AddSingleton<BridgeServer>();
		});
		var entryId = harness.Context.Config.AddEntry("VS Code");
		harness.Context.Config.SeedSecret(entryId, "pairing_token", Token);
		harness.Context.Config.SeedString(entryId, "port", port.ToString(CultureInfo.InvariantCulture));
		await harness.InitializeIntegrationsAsync();

		var extensionDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../../../vscode-extension"));
		var script = """
			const { BridgeClient } = require("./out/bridgeClient.js");
			const [port, token] = process.argv.slice(1);
			const c = new BridgeClient({ port: Number(port), token, windowId: "node",
				onRequest: async (method, params) => { if (params.command !== "ok") throw new Error("command '" + params.command + "' not found"); return null; },
				onStatus: () => {},
				onConnected: () => c.send({ type: "state", state: { focused: true, workspace: "interop", errors: 4 } }) });
			c.start();
			setTimeout(() => process.exit(0), 8000);
			""";
		using var node = Process.Start(new ProcessStartInfo("node", ["-e", script, port.ToString(CultureInfo.InvariantCulture), Token])
		{
			WorkingDirectory = extensionDir
		})!;

		try
		{
			// A real child process on a real clock, so this polls in real time rather than on the harness clock.
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (await ReadWorkspaceAsync(harness) != "interop")
			{
				Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the Node client should pair and report state");
				await Task.Delay(50);
			}

			var ok = await harness.Actions.ExecuteAsync("run-command", new Dictionary<string, object?> { ["command"] = "ok" });
			var missing = await harness.Actions.ExecuteAsync("run-command", new Dictionary<string, object?> { ["command"] = "missing" });

			Assert.That(ok.Succeeded, Is.True);
			Assert.That(missing.Succeeded, Is.False);
		}
		finally
		{
			node.Kill();
		}
	}

	private static async Task<string?> ReadWorkspaceAsync(PluginTestHarness harness)
		=> (await harness.Variables.GetAsync("workspace")).DataAs<VariableReadingDto>()?.Value.Text;
}
