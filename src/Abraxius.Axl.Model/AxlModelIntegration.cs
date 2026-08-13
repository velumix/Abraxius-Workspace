using Abraxius.Axl;
using Abraxius.Models;

namespace Abraxius.Axl.Model;

public static class AxlModelIntegration
{
    public static ModelRequest WithAxlOutput(
        this ModelRequest request,
        IEnumerable<string>? allowedCommands = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pack = AxlModelSchemaPack.Create(allowedCommands);
        var systemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
            ? pack.Text
            : $"{request.SystemPrompt}\n{pack.Text}";
        var metadata = request.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);
        metadata["axl.version"] = pack.Version.ToString();
        metadata["axl.schemas"] = string.Join(',', pack.Commands);
        return request with
        {
            SystemPrompt = systemPrompt,
            OutputFormat = ModelOutputFormat.Axl,
            Metadata = metadata
        };
    }

    public static AxlParseResult ParseModelResult(ModelResult result, AxlLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var candidate = string.IsNullOrWhiteSpace(result.StructuredJson) ? result.Text : result.StructuredJson;
        var parsed = AxlPipeline.ParseAndValidate(candidate, limits);
        if (parsed.IsSuccess)
        {
            return parsed;
        }

        var repaired = AxlRepairPipeline.Repair(candidate);
        return repaired.Changed ? AxlPipeline.ParseAndValidate(repaired.Text, limits) : parsed;
    }
}
