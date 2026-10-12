using DodecaDev.VsCode.Bridge;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;

namespace DodecaDev.VsCode.Actions;

/// <summary>Maps a failed bridge request onto the action result and dropdown error the user sees.</summary>
internal static class BridgeResults
{
	internal static ActionResult Failed(BridgeRequestException ex) => ex.Failure switch
	{
		BridgeFailure.NoWindow => ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.NoWindow()),
		BridgeFailure.Disconnected => ActionResult.Failed(ActionErrorCodes.NotConnected, Strings.Errors.Disconnected()),
		BridgeFailure.Timeout => ActionResult.Failed(ActionErrorCodes.Timeout, Strings.Errors.Timeout()),
		_ => ActionResult.Failed(ActionErrorCodes.ProviderRejected, Strings.Errors.Rejected(detail: ex.Detail ?? ""))
	};

	internal static LocalizedText Message(BridgeRequestException ex) => ex.Failure switch
	{
		BridgeFailure.NoWindow => Strings.Errors.NoWindow(),
		BridgeFailure.Disconnected => Strings.Errors.Disconnected(),
		BridgeFailure.Timeout => Strings.Errors.Timeout(),
		_ => Strings.Errors.Rejected(detail: ex.Detail ?? "")
	};
}
