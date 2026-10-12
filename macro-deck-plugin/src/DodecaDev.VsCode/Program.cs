using DodecaDev.VsCode;
using DodecaDev.VsCode.Bridge;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Microsoft.Extensions.DependencyInjection;

// Identity, description and icon are not set here: they come from manifest.json at the content root.
var builder = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<PluginIntegration>();

// One hub for the whole process: it outlives integration re-initialization, which only restarts it
// when the port or pairing token changed.
builder.Services.AddSingleton<BridgeServer>();

var plugin = builder.Build();
await plugin.RunAsync();
