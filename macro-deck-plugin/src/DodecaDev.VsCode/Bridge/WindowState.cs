using System.Text.Json.Serialization;

namespace DodecaDev.VsCode.Bridge;

/// <summary>What one VS Code window last reported about itself. Replaced as a whole on every report,
/// so readers never see half of an update.</summary>
internal sealed record WindowState
{
	internal static readonly WindowState Empty = new();

	[JsonPropertyName("focused")] public bool Focused { get; init; }
	[JsonPropertyName("appName")] public string? AppName { get; init; }
	[JsonPropertyName("workspace")] public string? Workspace { get; init; }

	/// <summary>The workspace file or first folder as a URI: what identifies "the same project" across
	/// window reloads, for pins and per-button targets. Null for a window with no folder open.</summary>
	[JsonPropertyName("workspaceKey")] public string? WorkspaceKey { get; init; }
	[JsonPropertyName("activeFile")] public string? ActiveFile { get; init; }
	[JsonPropertyName("language")] public string? Language { get; init; }
	[JsonPropertyName("gitBranch")] public string? GitBranch { get; init; }
	[JsonPropertyName("errors")] public int Errors { get; init; }
	[JsonPropertyName("warnings")] public int Warnings { get; init; }
	[JsonPropertyName("debugging")] public bool Debugging { get; init; }
	[JsonPropertyName("unsavedFiles")] public int UnsavedFiles { get; init; }
}

/// <summary>A command or task offered to a dropdown.</summary>
internal sealed record BridgeOption(
	[property: JsonPropertyName("id")] string Id,
	[property: JsonPropertyName("label")] string? Label);
