namespace Abraxius.Security;

public sealed record SandboxCapabilities(
    bool RestrictedProcess,
    bool IsolatedWorkspace,
    bool Container,
    bool RemoteSandbox)
{
    public bool Supports(SandboxLevel level) => level switch
    {
        SandboxLevel.None => true,
        SandboxLevel.RestrictedProcess => RestrictedProcess,
        SandboxLevel.IsolatedWorkspace => IsolatedWorkspace,
        SandboxLevel.Container => Container,
        SandboxLevel.RemoteSandbox => RemoteSandbox,
        _ => false
    };
}

public sealed record SandboxRequest(SecuritySubject Subject, SandboxLevel MinimumLevel, string Workspace, bool NetworkAllowed = false);
public sealed record SandboxLease(Guid Id, SandboxLevel Level, string Workspace, DateTimeOffset CreatedAt) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public interface ISandboxService
{
    SandboxCapabilities Capabilities { get; }
    ValueTask<SandboxLease?> AcquireAsync(SandboxRequest request, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceSandboxService(bool isolatedWorkspaceAvailable = true) : ISandboxService
{
    public SandboxCapabilities Capabilities { get; } = new(false, isolatedWorkspaceAvailable, false, false);
    public ValueTask<SandboxLease?> AcquireAsync(SandboxRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Capabilities.Supports(request.MinimumLevel)) return ValueTask.FromResult<SandboxLease?>(null);
        return ValueTask.FromResult<SandboxLease?>(new(Guid.NewGuid(), request.MinimumLevel, Path.GetFullPath(request.Workspace), DateTimeOffset.UtcNow));
    }
}

public sealed record SecureProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null,
    bool UseShell = false,
    SandboxLevel MinimumSandbox = SandboxLevel.None);

public static class SanitizedProcessEnvironment
{
    private static readonly string[] SafeNames = ["PATH", "DOTNET_ROOT", "LANG", "LC_ALL", "TMPDIR", "TEMP", "TMP", "SYSTEMROOT", "WINDIR"];
    public static IReadOnlyDictionary<string, string> Create(IReadOnlyDictionary<string, string>? explicitValues = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SafeNames) if (Environment.GetEnvironmentVariable(name) is { } value) result[name] = value;
        if (explicitValues is not null) foreach (var pair in explicitValues) result[pair.Key] = pair.Value;
        return result;
    }
}
