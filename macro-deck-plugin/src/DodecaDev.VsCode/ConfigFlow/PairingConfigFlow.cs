using System.Globalization;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;

namespace DodecaDev.VsCode.ConfigFlow;

/// <summary>
/// One step: the pairing token the companion extension shows (command "Macro Deck: Copy Pairing
/// Token"), and the port both sides agree on. Nothing can be checked here: VS Code connects to the
/// plugin, not the other way round, so a wrong token shows up as no window connecting.
/// </summary>
internal sealed class PairingConfigFlow : IConfigFlow
{
	internal const string PortField = "port";
	internal const string TokenField = "pairing_token";

	private const string StepId = "pairing";

	internal static bool TryParsePort(object? value, out int port)
	{
		port = 0;
		var parsed = value switch
		{
			int i => i,
			long l => l,
			double d when d == Math.Floor(d) => (long)d,
			string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
			_ => -1L
		};

		if (parsed is < 1024 or > 65535)
		{
			return false;
		}

		port = (int)parsed;
		return true;
	}

	private static ConfigFlowStep BuildStep() => new()
	{
		StepId = StepId,
		Title = Strings.ConfigFlow.Title(),
		Description = Strings.ConfigFlow.Description(),
		Fields =
		[
			ActionParameter.Secret(TokenField,
				label: Strings.ConfigFlow.Token.Label(),
				description: Strings.ConfigFlow.Token.Description(),
				required: true),
			// Text, not Number: GetStringAsync only reads values stored as JSON strings. SubmitAsync validates it.
			ActionParameter.Text(PortField,
				label: Strings.ConfigFlow.Port.Label(),
				description: Strings.ConfigFlow.Port.Description(),
				defaultValue: BridgeServer.DefaultPort.ToString(CultureInfo.InvariantCulture),
				required: true)
		]
	};

	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
		=> Task.FromResult(ConfigFlowResult.Step(BuildStep()));

	public Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		if (!string.Equals(stepId, StepId, StringComparison.Ordinal))
		{
			return Task.FromResult(ConfigFlowResult.Error(BuildStep(), Strings.ConfigFlow.UnknownStep()));
		}

		if (!TryParsePort(input.GetValueOrDefault(PortField), out _))
		{
			var message = Strings.ConfigFlow.Port.Invalid();
			return Task.FromResult(ConfigFlowResult.Error(BuildStep(), message,
				new Dictionary<string, LocalizedText> { [PortField] = message }));
		}

		// Reconfiguring may leave the secret blank to keep the stored one; a new entry may not.
		var isReconfigure = (context as IConfigFlowEntryContext)?.EntryTitle is not null;
		var token = (input.GetValueOrDefault(TokenField) as string ?? "").Trim();
		if (!isReconfigure && token.Length < 16)
		{
			var message = Strings.ConfigFlow.Token.Invalid();
			return Task.FromResult(ConfigFlowResult.Error(BuildStep(), message,
				new Dictionary<string, LocalizedText> { [TokenField] = message }));
		}

		// Deliberately a plain string: the host stores it as the entry's name, which the user can rename.
		return Task.FromResult(ConfigFlowResult.Complete("VS Code"));
	}
}
