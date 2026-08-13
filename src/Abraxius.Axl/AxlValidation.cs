using System.Collections.Immutable;

namespace Abraxius.Axl;

public static class AxlValidator
{
    public static ImmutableArray<AxlDiagnostic> Validate(
        AxlDocument document,
        AxlValidationOptions? options = null,
        IAxlSchemaRegistry? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new AxlValidationOptions();
        var limits = options.Limits ?? new AxlLimits();
        var diagnostics = ImmutableArray.CreateBuilder<AxlDiagnostic>();
        if (document.Version.Major != AxlVersion.Current.Major || document.Version.Minor > AxlVersion.Current.Minor)
        {
            diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.UnsupportedVersion, $"AXL version {document.Version} is not supported."));
        }

        if (document.Commands.Length > limits.MaxCommands)
        {
            diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.LimitExceeded, "AXL command count exceeds the configured limit."));
        }

        schemas ??= AxlSchemaRegistry.CreateDefault();
        var ids = new HashSet<AxlCommandId>();
        foreach (var command in document.Commands)
        {
            if (command.Id is { } id)
            {
                ids.Add(id);
            }
        }

        var seenIds = new HashSet<AxlCommandId>();
        foreach (var command in document.Commands)
        {
            if (command.Id is { } id && !seenIds.Add(id))
            {
                diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.DuplicateCommandId, $"Command ID {id} occurs more than once."));
            }

            if (!schemas.TryGet(command.Name, out _))
            {
                diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.UnknownCommand, $"No schema is registered for '{command.Name}'."));
            }

            switch (command)
            {
                case AxlCapabilityCall call:
                    if (call.Capability.Kind != AxlReferenceKind.Capability)
                    {
                        diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.InvalidReference, "Capability calls require @cap references."));
                    }

                    if (call.Mutation && !options.AllowMutations)
                    {
                        diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.PolicyDenied, "Mutation-capable AXL is disabled by the validation policy."));
                    }

                    if (options.RequireRegisteredCapabilities && (options.AllowedCapabilities is null || !options.AllowedCapabilities.Contains(call.Capability.Value)))
                    {
                        diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.PolicyDenied, $"Capability '{call.Capability}' is not registered for this compilation context."));
                    }

                    break;
                case AxlDelegation delegation when delegation.Agent.Kind != AxlReferenceKind.Agent:
                    diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.InvalidReference, "Delegation requires an @agent reference."));
                    break;
            }

            foreach (var dependency in command.Dependencies)
            {
                if (dependency.Kind != AxlReferenceKind.Command)
                {
                    diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.InvalidDependency, $"Dependency {dependency} is not a command reference."));
                }
                else if (!ids.Contains(new AxlCommandId(dependency.Value)))
                {
                    diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.UnknownCommandReference, $"Dependency {dependency} does not identify a known command."));
                }
            }
        }

        return diagnostics.ToImmutable();
    }
}

public static class AxlPipeline
{
    public static AxlParseResult ParseAndValidate(
        string text,
        AxlLimits? limits = null,
        AxlValidationOptions? options = null,
        IAxlSchemaRegistry? schemas = null)
    {
        var parsed = AxlParser.Parse(text, limits);
        if (!parsed.IsSuccess || parsed.Document is null)
        {
            return parsed;
        }

        var diagnostics = AxlValidator.Validate(parsed.Document, options, schemas);
        return diagnostics.Length == 0
            ? parsed
            : parsed with { Status = AxlParseStatus.Invalid, Document = null, Diagnostics = diagnostics };
    }
}

public sealed record AxlRepairResult(
    bool Changed,
    string Text,
    ImmutableArray<AxlDiagnostic> Diagnostics);

/// <summary>Conservative model-output cleanup. It never invents commands or fields.</summary>
public static class AxlRepairPipeline
{
    public static AxlRepairResult Repair(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal) && trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0 && trimmed[..firstNewline].Trim() is "```axl" or "```text" or "```")
            {
                var repaired = trimmed[(firstNewline + 1)..^3].Trim();
                if (repaired.StartsWith("axl/", StringComparison.Ordinal))
                {
                    return new(true, repaired, ImmutableArray<AxlDiagnostic>.Empty);
                }
            }
        }

        return new(false, text, ImmutableArray<AxlDiagnostic>.Empty);
    }
}
