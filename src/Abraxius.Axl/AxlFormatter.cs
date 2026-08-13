using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Abraxius.Axl;

public static class AxlFormatter
{
    public static string Compact(AxlDocument document) => Format(document, AxlFormatMode.Compact);

    public static string Pretty(AxlDocument document) => Format(document, AxlFormatMode.Pretty);

    public static string Diagnostic(AxlDocument document) => Format(document, AxlFormatMode.Diagnostic);

    internal static string ValueToCanonical(AxlValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder();
        AppendValue(builder, value);
        return builder.ToString();
    }

    public static string Format(AxlDocument document, AxlFormatMode mode = AxlFormatMode.Compact)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        builder.Append(document.Version);
        builder.Append('\n');

        if (document.Commands.Length == 1)
        {
            AppendCommand(builder, document.Commands[0], mode == AxlFormatMode.Pretty ? 0 : -1, mode);
            return builder.ToString();
        }

        builder.Append("batch {");
        if (mode != AxlFormatMode.Compact)
        {
            builder.Append('\n');
        }

        for (var index = 0; index < document.Commands.Length; index++)
        {
            if (mode != AxlFormatMode.Compact)
            {
                builder.Append("  ");
            }

            AppendCommand(builder, document.Commands[index], mode == AxlFormatMode.Compact ? -1 : 0, mode);
            if (index + 1 < document.Commands.Length || mode != AxlFormatMode.Compact)
            {
                builder.Append('\n');
            }
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendCommand(StringBuilder builder, AxlCommand command, int indent, AxlFormatMode mode)
    {
        if (command.Id is { } id)
        {
            builder.Append(id);
            builder.Append(' ');
        }

        switch (command)
        {
            case AxlFindCode find:
                builder.Append("find code q=");
                AppendValue(builder, new AxlValue.Text(find.Query));
                builder.Append(" lim=").Append(find.Limit.ToString(CultureInfo.InvariantCulture));
                if (find.Scope is { } scope)
                {
                    builder.Append(" scope=").Append(scope);
                }

                break;
            case AxlCapabilityCall call:
                builder.Append("call ").Append(call.Capability);
                builder.Append(" op=").Append(Identifier(call.Operation));
                builder.Append(" target=");
                AppendValue(builder, new AxlValue.Text(call.Target));
                if (call.Mutation)
                {
                    builder.Append(" mutation=true");
                }

                foreach (var pair in (call.Parameters ?? ImmutableDictionary<string, AxlValue>.Empty).OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append(' ').Append(pair.Key).Append('=');
                    AppendValue(builder, pair.Value);
                }

                break;
            case AxlMemoryQuery memory:
                builder.Append("memory query q=");
                AppendValue(builder, new AxlValue.Text(memory.Query));
                builder.Append(" lim=").Append(memory.Limit.ToString(CultureInfo.InvariantCulture));
                if (!memory.Scopes.IsDefaultOrEmpty)
                {
                    builder.Append(" scope=");
                    AppendValue(builder, new AxlValue.List(memory.Scopes.Select(static value => (AxlValue)new AxlValue.Text(value)).ToImmutableArray()));
                }

                break;
            case AxlSynthesis synthesis:
                builder.Append("synth obj=");
                AppendValue(builder, new AxlValue.Text(synthesis.Objective));
                AppendReferences(builder, "dep", synthesis.Inputs);
                break;
            case AxlVerification verification:
                builder.Append("verify obj=");
                AppendValue(builder, new AxlValue.Text(verification.Objective));
                AppendReferences(builder, "dep", verification.Inputs);
                if (!string.IsNullOrWhiteSpace(verification.Profile))
                {
                    builder.Append(" profile=").Append(Identifier(verification.Profile));
                }

                break;
            case AxlIntent intent:
                builder.Append("intent obj=");
                AppendValue(builder, new AxlValue.Text(intent.Objective));
                builder.Append(" pri=").Append(Identifier(intent.Priority.ToString()));
                if (intent.Attributes is { Count: > 0 })
                {
                    builder.Append(" attrs=");
                    AppendValue(builder, new AxlValue.Record(intent.Attributes!.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => new KeyValuePair<string, AxlValue>(pair.Key, new AxlValue.Text(pair.Value))).ToImmutableArray()));
                }

                break;
            case AxlDelegation delegation:
                builder.Append("delegate agent=").Append(delegation.Agent).Append(" obj=");
                AppendValue(builder, new AxlValue.Text(delegation.Objective));
                AppendReferences(builder, "ev", delegation.Evidence);
                builder.Append(" mode=").Append(Identifier(delegation.Mode));
                break;
            case AxlResult result:
                builder.Append("ret ref=").Append(result.Correlation).Append(" status=").Append(result.Succeeded ? "ok" : "fail");
                AppendReferences(builder, "ev", result.References);
                if (!string.IsNullOrWhiteSpace(result.ErrorCode))
                {
                    builder.Append(" err=");
                    AppendValue(builder, new AxlValue.Text(result.ErrorCode));
                }

                break;
            case AxlState state:
                builder.Append("state ref=").Append(state.Target).Append(" status=").Append(Identifier(state.State));
                break;
            case AxlSkill skill:
                builder.Append("skill id=");
                AppendValue(builder, new AxlValue.Text(skill.SkillName));
                builder.Append(" ver=");
                AppendValue(builder, new AxlValue.Text(skill.Version));
                AppendStrings(builder, "trigger", skill.SafeTriggers);
                AppendStrings(builder, "requires", skill.SafeRequires);
                AppendStrings(builder, "steps", skill.SafeSteps);
                AppendStrings(builder, "verify", skill.SafeVerify);
                builder.Append(" safety=").Append(Identifier(skill.Safety));
                break;
            default:
                throw new InvalidOperationException($"Unsupported AXL command type {command.GetType().Name}.");
        }
    }

    private static void AppendReferences(StringBuilder builder, string name, ImmutableArray<AxlReference> references)
    {
        if (references.IsDefaultOrEmpty)
        {
            return;
        }

        builder.Append(' ').Append(name).Append('=');
        AppendValue(builder, new AxlValue.List(references.Select(static reference => (AxlValue)new AxlValue.ReferenceValue(reference)).ToImmutableArray()));
    }

    private static void AppendStrings(StringBuilder builder, string name, ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty) return;
        builder.Append(' ').Append(name).Append('=');
        AppendValue(builder, new AxlValue.List(values.Select(static value => (AxlValue)new AxlValue.Text(value)).ToImmutableArray()));
    }

    private static void AppendValue(StringBuilder builder, AxlValue value)
    {
        switch (value)
        {
            case AxlValue.Text text:
                builder.Append('"');
                foreach (var character in text.Value)
                {
                    builder.Append(character switch
                    {
                        '"' => "\\\"",
                        '\\' => "\\\\",
                        '\n' => "\\n",
                        '\r' => "\\r",
                        '\t' => "\\t",
                        _ => character.ToString()
                    });
                }

                builder.Append('"');
                break;
            case AxlValue.SignedInteger integer:
                builder.Append(integer.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case AxlValue.UnsignedInteger unsigned:
                builder.Append(unsigned.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case AxlValue.DecimalValue decimalValue:
                builder.Append(decimalValue.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case AxlValue.BooleanValue boolean:
                builder.Append(boolean.Value ? "true" : "false");
                break;
            case AxlValue.ReferenceValue reference:
                builder.Append(reference.Value);
                break;
            case AxlValue.Identifier identifier:
                builder.Append(Identifier(identifier.Value));
                break;
            case AxlValue.List list:
                builder.Append('[');
                for (var index = 0; index < list.Values.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(' ');
                    }

                    AppendValue(builder, list.Values[index]);
                }

                builder.Append(']');
                break;
            case AxlValue.Record record:
                builder.Append('{');
                for (var index = 0; index < record.Fields.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(' ');
                    }

                    var pair = record.Fields[index];
                    builder.Append(pair.Key).Append('=');
                    AppendValue(builder, pair.Value);
                }

                builder.Append('}');
                break;
            case AxlValue.Null:
                builder.Append("null");
                break;
            default:
                throw new InvalidOperationException($"Unsupported AXL value type {value.GetType().Name}.");
        }
    }

    private static string Identifier(string value) =>
        value.Length > 0 && value.All(static character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/')
            ? value
            : $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

public sealed class AxlDefaultFormatter : IAxlFormatter
{
    public string Format(AxlDocument document, AxlFormatMode mode = AxlFormatMode.Compact) => AxlFormatter.Format(document, mode);
}
