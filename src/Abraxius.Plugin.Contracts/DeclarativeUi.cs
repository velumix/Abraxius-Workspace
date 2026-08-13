using System.Collections.Immutable;

namespace Abraxius.Plugin.Contracts;

public enum PluginViewComponentKind { Text, Heading, Badge, Icon, Stack, Grid, Separator, Button, Toggle, TextInput, Select, Form, List, Table, KeyValue, Progress, Metric, CodeBlock, Markdown, Image, Chart, EmptyState }

public sealed record PluginViewComponent(
    string Id,
    PluginViewComponentKind Kind,
    string? Text = null,
    string? ValuePath = null,
    string? CommandId = null,
    ImmutableDictionary<string, string>? Properties = null,
    ImmutableArray<PluginViewComponent>? Children = null)
{
    public ImmutableArray<PluginViewComponent> SafeChildren => Children ?? [];
}

public sealed record PluginViewDescriptor(string Id, string Title, PluginViewComponent Root, int MaximumRows = 1_000, int PageSize = 100);
public sealed record PluginViewState(string ViewId, long Version, ImmutableDictionary<string, string> Values, ImmutableArray<ImmutableDictionary<string, string>> Rows, bool HasMore = false, string? ContinuationToken = null);
