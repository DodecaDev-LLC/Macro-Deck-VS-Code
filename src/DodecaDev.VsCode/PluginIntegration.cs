using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace DodecaDev.VsCode;

/// <summary>
/// The plugin's one integration. It declares a single example action: add more to <see cref="Actions"/>,
/// and opt into a capability by implementing its interface here (<c>IVariableProvider</c>,
/// <c>IEventProvider</c>, <c>IConfigFlowProvider</c>, and so on).
/// </summary>
public sealed class PluginIntegration : IPluginIntegration
{
	private readonly ILogger _logger;

	// Built by DI, so anything the container knows can be taken here: IHttpClientFactory, IOptions<T>,
	// PluginMetadata, IPluginCatalogNotifier.
	public PluginIntegration(ILogger logger)
	{
		_logger = logger.ForContext<PluginIntegration>();
		Actions = [new LogMessageAction(logger)];
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	/// <summary>
	/// Runs once the session is established, and again after a non-resume reconnect or a configuration
	/// change, so it has to be safe to run repeatedly against an already-initialized process.
	/// </summary>
	public Task InitializeAsync(IIntegrationContext context)
	{
		_logger.Information("Initialized.");
		return Task.CompletedTask;
	}

	public Task ShutdownAsync() => Task.CompletedTask;
}
