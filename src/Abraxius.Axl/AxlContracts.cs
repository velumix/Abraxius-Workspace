using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Abraxius.Core;
using Abraxius.Protocol;

namespace Abraxius.Axl;

public readonly record struct AxlVersion(int Major, int Minor = 0)
{
    public static AxlVersion Current => new(1, 0);

    public bool IsCompatibleWith(AxlVersion requested) => Major == requested.Major && Minor >= requested.Minor;

    public override string ToString() => Minor == 0 ? $"axl/{Major}" : $"axl/{Major}.{Minor}";

    public static bool TryParse(string? value, out AxlVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("axl/", StringComparison.Ordinal))
        {
            return false;
        }

        var numeric = value[4..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (numeric.Length is < 1 or > 2 || !int.TryParse(numeric[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return false;
        }

        var minor = numeric.Length == 2 && int.TryParse(numeric[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMinor)
            ? parsedMinor
            : numeric.Length == 1 ? 0 : -1;
        if (major < 0 || minor < 0)
        {
            return false;
        }

        version = new AxlVersion(major, minor);
        return true;
    }
}

public enum AxlParseStatus
{
    Success,
    Incomplete,
    Invalid,
    UnsupportedVersion
}

public enum AxlDiagnosticSeverity
{
    Error,
    Warning,
    Info
}

public enum AxlDiagnosticCode
{
    InvalidHeader,
    UnsupportedVersion,
    UnexpectedEnd,
    UnexpectedToken,
    InvalidIdentifier,
    InvalidString,
    InvalidNumber,
    InvalidReference,
    UnknownCommand,
    UnknownField,
    DuplicateField,
    MissingField,
    InvalidValue,
    DuplicateCommandId,
    UnknownCommandReference,
    InvalidDependency,
    UnsupportedOperation,
    SemanticConflict,
    LimitExceeded,
    InvalidUtf8,
    BinaryInvalid,
    BinaryUnsupportedVersion,
    BinaryTooLarge,
    PolicyDenied
}

public readonly record struct AxlSourceSpan(int Offset, int Length, int Line, int Column)
{
    public int End => Offset + Length;
}

public sealed record AxlDiagnostic(
    AxlDiagnosticSeverity Severity,
    AxlDiagnosticCode Code,
    string Message,
    AxlSourceSpan Span = default,
    string? Expected = null,
    string? Actual = null)
{
    public override string ToString() => $"AXL{(int)Code:000} {Code} at {Span.Line}:{Span.Column} {Message}";
}

public sealed record AxlParseResult(
    AxlParseStatus Status,
    AxlDocument? Document,
    ImmutableArray<AxlDiagnostic> Diagnostics)
{
    public bool IsSuccess => Status == AxlParseStatus.Success && Document is not null && !Diagnostics.Any(static d => d.Severity == AxlDiagnosticSeverity.Error);

    public static AxlParseResult Success(AxlDocument document) => new(AxlParseStatus.Success, document, ImmutableArray<AxlDiagnostic>.Empty);
}

public enum AxlReferenceKind
{
    Command,
    Task,
    Result,
    Evidence,
    Artifact,
    Capability,
    Agent,
    Model,
    Project,
    Secret,
    Concept,
    Unknown
}

public readonly record struct AxlReference(AxlReferenceKind Kind, string Value)
{
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Kind switch
    {
        AxlReferenceKind.Command => $"c#{Value}",
        AxlReferenceKind.Task => $"t#{Value}",
        AxlReferenceKind.Result => $"r#{Value}",
        AxlReferenceKind.Evidence => $"e#{Value}",
        AxlReferenceKind.Artifact => $"a#{Value}",
        AxlReferenceKind.Capability => $"@cap:{Value}",
        AxlReferenceKind.Agent => $"@agent:{Value}",
        AxlReferenceKind.Model => $"@model:{Value}",
        AxlReferenceKind.Project => "@project",
        AxlReferenceKind.Secret => $"@secret:{Value}",
        AxlReferenceKind.Concept => $"@concept:{Value}",
        _ => Value
    };

    public static bool TryParse(string? text, out AxlReference reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.Equals("@project", StringComparison.Ordinal))
        {
            reference = new(AxlReferenceKind.Project, "project");
            return true;
        }

        var kind = value switch
        {
            _ when value.StartsWith("c#", StringComparison.Ordinal) => AxlReferenceKind.Command,
            _ when value.StartsWith("t#", StringComparison.Ordinal) => AxlReferenceKind.Task,
            _ when value.StartsWith("r#", StringComparison.Ordinal) => AxlReferenceKind.Result,
            _ when value.StartsWith("e#", StringComparison.Ordinal) => AxlReferenceKind.Evidence,
            _ when value.StartsWith("a#", StringComparison.Ordinal) => AxlReferenceKind.Artifact,
            _ when value.StartsWith("@cap:", StringComparison.Ordinal) => AxlReferenceKind.Capability,
            _ when value.StartsWith("@agent:", StringComparison.Ordinal) => AxlReferenceKind.Agent,
            _ when value.StartsWith("@model:", StringComparison.Ordinal) => AxlReferenceKind.Model,
            _ when value.StartsWith("@secret:", StringComparison.Ordinal) => AxlReferenceKind.Secret,
            _ when value.StartsWith("@concept:", StringComparison.Ordinal) => AxlReferenceKind.Concept,
            _ => AxlReferenceKind.Unknown
        };
        if (kind == AxlReferenceKind.Unknown)
        {
            return false;
        }

        var prefixLength = kind switch
        {
            AxlReferenceKind.Command or AxlReferenceKind.Task or AxlReferenceKind.Result or AxlReferenceKind.Evidence or AxlReferenceKind.Artifact => 2,
            AxlReferenceKind.Capability => 5,
            AxlReferenceKind.Agent => 7,
            AxlReferenceKind.Model => 7,
            AxlReferenceKind.Secret => 8,
            AxlReferenceKind.Concept => 9,
            _ => 0
        };
        var payload = value[prefixLength..];
        if (payload.Length == 0 || payload.Any(static c => char.IsWhiteSpace(c) || c is ':' or '[' or ']' or '{' or '}'))
        {
            return false;
        }

        reference = new(kind, payload);
        return true;
    }
}

public readonly record struct AxlCommandId(string Value)
{
    public override string ToString() => $"c#{Value}";
    public static bool TryParse(string? value, out AxlCommandId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("c#", StringComparison.Ordinal) || value.Length <= 2)
        {
            return false;
        }

        var suffix = value[2..];
        if (!suffix.All(static c => char.IsLetterOrDigit(c) || c is '_' or '-'))
        {
            return false;
        }

        id = new AxlCommandId(suffix);
        return true;
    }
}

public abstract record AxlValue
{
    public sealed record Text(string Value) : AxlValue;
    public sealed record SignedInteger(long Value) : AxlValue;
    public sealed record UnsignedInteger(ulong Value) : AxlValue;
    public sealed record DecimalValue(decimal Value) : AxlValue;
    public sealed record BooleanValue(bool Value) : AxlValue;
    public sealed record ReferenceValue(AxlReference Value) : AxlValue;
    public sealed record Identifier(string Value) : AxlValue;
    public sealed record List(ImmutableArray<AxlValue> Values) : AxlValue;
    public sealed record Record(ImmutableArray<KeyValuePair<string, AxlValue>> Fields) : AxlValue;
    public sealed record Null : AxlValue;
}

public abstract record AxlCommand(AxlCommandId? Id)
{
    public abstract string Name { get; }
    public virtual ImmutableArray<AxlReference> Dependencies => ImmutableArray<AxlReference>.Empty;
}

public sealed record AxlFindCode(
    string Query,
    int Limit = 20,
    AxlReference? Scope = null,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "find.code";
}

public sealed record AxlCapabilityCall(
    AxlReference Capability,
    string Operation,
    string Target = "current_project",
    ImmutableDictionary<string, AxlValue>? Parameters = null,
    bool Mutation = false,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "call";
}

public sealed record AxlMemoryQuery(
    string Query,
    int Limit = 8,
    ImmutableArray<string> Scopes = default,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "memory.query";
}

public sealed record AxlSynthesis(
    string Objective,
    ImmutableArray<AxlReference> Inputs = default,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "synth";
    public override ImmutableArray<AxlReference> Dependencies => Inputs.IsDefault ? ImmutableArray<AxlReference>.Empty : Inputs;
}

public sealed record AxlVerification(
    string Objective = "verify result",
    ImmutableArray<AxlReference> Inputs = default,
    string? Profile = null,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "verify";
    public override ImmutableArray<AxlReference> Dependencies => Inputs.IsDefault ? ImmutableArray<AxlReference>.Empty : Inputs;
}

public sealed record AxlIntent(
    string Objective,
    WorkPriority Priority = WorkPriority.Interactive,
    ImmutableDictionary<string, string>? Attributes = null,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "intent";
}

public sealed record AxlDelegation(
    AxlReference Agent,
    string Objective,
    ImmutableArray<AxlReference> Evidence = default,
    string Mode = "readonly",
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "delegate";
}

public sealed record AxlResult(
    AxlReference Correlation,
    bool Succeeded,
    ImmutableArray<AxlReference> References = default,
    string? ErrorCode = null,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "ret";
}

public sealed record AxlState(
    AxlReference Target,
    string State,
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "state";
}

/// <summary>
/// Declarative Skill metadata. It is data and schema, never an executable escape hatch.
/// The Skill runtime performs a separate validation and policy pass before compiling steps.
/// </summary>
public sealed record AxlSkill(
    string SkillName,
    string Version,
    ImmutableArray<string> Triggers = default,
    ImmutableArray<string> Requires = default,
    ImmutableArray<string> Steps = default,
    ImmutableArray<string> Verify = default,
    string Safety = "readonly",
    AxlCommandId? CommandId = null) : AxlCommand(CommandId)
{
    public override string Name => "skill";
    public ImmutableArray<string> SafeTriggers => Triggers.IsDefault ? ImmutableArray<string>.Empty : Triggers;
    public ImmutableArray<string> SafeRequires => Requires.IsDefault ? ImmutableArray<string>.Empty : Requires;
    public ImmutableArray<string> SafeSteps => Steps.IsDefault ? ImmutableArray<string>.Empty : Steps;
    public ImmutableArray<string> SafeVerify => Verify.IsDefault ? ImmutableArray<string>.Empty : Verify;
}

public sealed record AxlDocument(
    AxlVersion Version,
    ImmutableArray<AxlCommand> Commands,
    string? SourceHash = null)
{
    public static AxlDocument Empty => new(AxlVersion.Current, ImmutableArray<AxlCommand>.Empty);

    public string SemanticHash()
    {
        var canonical = AxlFormatter.Compact(this);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record AxlLimits(
    int MaxDocumentBytes = 1_048_576,
    int MaxStringLength = 262_144,
    int MaxCommands = 10_000,
    int MaxListItems = 10_000,
    int MaxRecordFields = 512,
    int MaxNestingDepth = 32);

public sealed record AxlValidationOptions(
    bool AllowMutations = false,
    bool RequireRegisteredCapabilities = false,
    AxlLimits? Limits = null,
    IReadOnlySet<string>? AllowedCapabilities = null);

public sealed record AxlSchema(
    string Name,
    ImmutableHashSet<string> RequiredFields,
    ImmutableHashSet<string> OptionalFields,
    bool AllowsExtensions = false)
{
    public bool Allows(string field) => RequiredFields.Contains(field) || OptionalFields.Contains(field) || AllowsExtensions;
}

public interface IAxlSchemaRegistry
{
    bool TryGet(string name, out AxlSchema schema);
    ImmutableArray<AxlSchema> Schemas { get; }
}

public sealed class AxlSchemaRegistry : IAxlSchemaRegistry
{
    private readonly ImmutableDictionary<string, AxlSchema> _schemas;

    private AxlSchemaRegistry(IEnumerable<AxlSchema> schemas) => _schemas = schemas.ToImmutableDictionary(static schema => schema.Name, StringComparer.Ordinal);

    public ImmutableArray<AxlSchema> Schemas => _schemas.Values.OrderBy(static schema => schema.Name, StringComparer.Ordinal).ToImmutableArray();

    public bool TryGet(string name, out AxlSchema schema) => _schemas.TryGetValue(name, out schema!);

    public static AxlSchemaRegistry CreateDefault() => new([
        new("find.code", ["q"], ["lim", "scope"]),
        new("call", [], ["cap", "op", "target", "mutation", "args"], AllowsExtensions: true),
        new("memory.query", ["q"], ["lim", "scope"]),
        new("synth", [], ["obj", "dep"]),
        new("verify", [], ["obj", "dep", "profile"]),
        new("intent", ["obj"], ["pri", "attrs"]),
        new("delegate", ["agent", "obj"], ["ev", "mode"]),
        new("ret", ["ref", "status"], ["ev", "err"]),
        new("state", ["ref", "status"], []),
        new("skill", ["id", "ver"], ["trigger", "requires", "steps", "verify", "safety"])
    ]);
}

public interface IAxlFormatter
{
    string Format(AxlDocument document, AxlFormatMode mode = AxlFormatMode.Compact);
}

public enum AxlFormatMode
{
    Compact,
    Pretty,
    Diagnostic
}

public interface IAxlExecutionCompiler
{
    AxlCompilationResult Compile(AxlDocument document, AxlCompilationContext context, AxlValidationOptions? options = null);
}

public sealed record AxlCompilationContext(ExecutionId ExecutionId, CorrelationId CorrelationId);

public sealed record AxlCompilationResult(
    Intent? Intent,
    ExecutionGraph? Graph,
    ImmutableDictionary<AxlCommandId, NodeId> NodeMap,
    ImmutableArray<AxlDiagnostic> Diagnostics)
{
    public bool IsSuccess => Diagnostics.All(static diagnostic => diagnostic.Severity != AxlDiagnosticSeverity.Error);
}

public sealed record AxlModelSchemaPack(
    AxlVersion Version,
    ImmutableArray<string> Commands,
    string Text)
{
    public static AxlModelSchemaPack Create(IEnumerable<string>? commands = null)
    {
        var selected = (commands ?? ["find.code", "call", "memory.query", "synth", "verify", "skill"]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        var signatures = selected.Select(static command => command switch
        {
            "find.code" => "find.code(q:str,lim:int?,scope:ref?)",
            "call" => "call(cap:ref,op:id?,target:str?,args:record?)",
            "memory.query" => "memory.query(q:str,lim:int?,scope:list?)",
            "synth" => "synth(obj:str?,dep:list?)",
            "verify" => "verify(obj:str?,dep:list?,profile:id?)",
            "skill" => "skill(id:id,ver:semver,trigger:list?,requires:list?,steps:list?,verify:list?,safety:id?)",
            _ => $"{command}(...)"
        });
        var text = $"axl/{AxlVersion.Current.Major} schemas=" + string.Join(',', selected) + " forms=" + string.Join(';', signatures);
        return new(AxlVersion.Current, selected, text);
    }
}
