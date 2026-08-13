using System.Text.Json;
using System.Text.Json.Serialization;

namespace Abraxius.Protocol;

/// <summary>Shared JSON options for transport and persistence-facing protocol contracts.</summary>
public static class ProtocolJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new CapabilityIdJsonConverter());
        options.Converters.Add(new GuidIdentifierJsonConverterFactory());
        return options;
    }
}

internal sealed class CapabilityIdJsonConverter : JsonConverter<CapabilityId>
{
    public override CapabilityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Capability ID cannot be null."));

    public override void Write(Utf8JsonWriter writer, CapabilityId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

internal sealed class GuidIdentifierJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(TaskId) ||
        typeToConvert == typeof(IntentId) ||
        typeToConvert == typeof(ModelRequestId) ||
        typeToConvert == typeof(ExecutionId) ||
        typeToConvert == typeof(CorrelationId) ||
        typeToConvert == typeof(NodeId) ||
        typeToConvert == typeof(AgentId) ||
        typeToConvert == typeof(EvidenceId) ||
        typeToConvert == typeof(ResultId) ||
        typeToConvert == typeof(ArtifactId) ||
        typeToConvert == typeof(SpeculationGroupId);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => typeToConvert == typeof(TaskId)
            ? new GuidIdentifierJsonConverter<TaskId>(static value => new TaskId(value), static identifier => identifier.Value)
            : typeToConvert == typeof(IntentId)
                ? new GuidIdentifierJsonConverter<IntentId>(static value => new IntentId(value), static identifier => identifier.Value)
                : typeToConvert == typeof(ModelRequestId)
                    ? new GuidIdentifierJsonConverter<ModelRequestId>(static value => new ModelRequestId(value), static identifier => identifier.Value)
                    : typeToConvert == typeof(ExecutionId)
                ? new GuidIdentifierJsonConverter<ExecutionId>(static value => new ExecutionId(value), static identifier => identifier.Value)
                : typeToConvert == typeof(CorrelationId)
                    ? new GuidIdentifierJsonConverter<CorrelationId>(static value => new CorrelationId(value), static identifier => identifier.Value)
                    : typeToConvert == typeof(NodeId)
                        ? new GuidIdentifierJsonConverter<NodeId>(static value => new NodeId(value), static identifier => identifier.Value)
                        : typeToConvert == typeof(AgentId)
                            ? new GuidIdentifierJsonConverter<AgentId>(static value => new AgentId(value), static identifier => identifier.Value)
                            : typeToConvert == typeof(EvidenceId)
                                ? new GuidIdentifierJsonConverter<EvidenceId>(static value => new EvidenceId(value), static identifier => identifier.Value)
                                : typeToConvert == typeof(ResultId)
                                    ? new GuidIdentifierJsonConverter<ResultId>(static value => new ResultId(value), static identifier => identifier.Value)
                                    : typeToConvert == typeof(ArtifactId)
                                        ? new GuidIdentifierJsonConverter<ArtifactId>(static value => new ArtifactId(value), static identifier => identifier.Value)
                                        : new GuidIdentifierJsonConverter<SpeculationGroupId>(static value => new SpeculationGroupId(value), static identifier => identifier.Value);
}

internal sealed class GuidIdentifierJsonConverter<T>(Func<Guid, T> factory, Func<T, Guid> valueSelector) : JsonConverter<T>
    where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        factory(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(valueSelector(value));
}
