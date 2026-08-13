using System.Collections.Immutable;
using Abraxius.Fabric;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugins;

public static class PluginFabricAdapter
{
    public static FabricNodeDescriptor WithPlugins(this FabricNodeDescriptor node, PluginRuntime plugins)
    {
        var pluginCapabilities = plugins.Contributions.Contributions
            .Where(static item => item.Kind == PluginContributionKind.CapabilityProvider && item.Descriptor is PluginCapabilityDescriptor)
            .Select(item => new FabricCapability($"plugin/{item.PluginId.Value}/{item.LocalId}", item.PluginVersion.ToString(), ((PluginCapabilityDescriptor)item.Descriptor).SideEffect == PluginCapabilitySideEffect.ReadOnly,
                ImmutableDictionary<string, string>.Empty.Add("plugin.id", item.PluginId.Value).Add("plugin.version", item.PluginVersion.ToString()).Add("contribution.id", item.LocalId)))
            .ToImmutableArray();
        return node with { Capabilities = node.Capabilities.AddRange(pluginCapabilities) };
    }
}
