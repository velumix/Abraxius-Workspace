namespace Abraxius.Agents;

/// <summary>Workspace ownership is explicit so parallel builders never accidentally share mutable state.</summary>
public sealed record WorkspaceRequest(
    string RepositoryPath,
    WorkspacePolicy Policy,
    MissionId MissionId,
    AssignmentId AssignmentId,
    string? BaseRevision = null);

public sealed record WorkspaceLease(
    string Path,
    WorkspacePolicy Policy,
    MissionId MissionId,
    AssignmentId AssignmentId,
    bool IsIsolated,
    string? BaseRevision = null);

public interface IWorkspaceIsolationService
{
    ValueTask<WorkspaceLease> CreateAsync(WorkspaceRequest request, CancellationToken cancellationToken = default);
    ValueTask<string> InspectDiffAsync(WorkspaceLease workspace, CancellationToken cancellationToken = default);
    ValueTask IntegrateAsync(WorkspaceLease workspace, CancellationToken cancellationToken = default);
    ValueTask CleanupAsync(WorkspaceLease workspace, CancellationToken cancellationToken = default);
}

/// <summary>
/// Safe local foundation used when a host has no Git adapter. It allocates unique workspace identities,
/// but does not mutate a repository. A platform Git adapter can implement the same boundary.
/// </summary>
public sealed class ManagedWorkspaceIsolationService : IWorkspaceIsolationService
{
    private readonly string _root;
    public ManagedWorkspaceIsolationService(string root) => _root = root;

    public ValueTask<WorkspaceLease> CreateAsync(WorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.RepositoryPath)) throw new ArgumentException("Repository path is required.", nameof(request));
        var isolated = request.Policy is WorkspacePolicy.IsolatedWorktree or WorkspacePolicy.TemporaryWorkspace;
        var path = isolated
            ? Path.Combine(_root, "missions", request.MissionId.ToString(), request.AssignmentId.ToString())
            : request.RepositoryPath;
        return ValueTask.FromResult(new WorkspaceLease(path, request.Policy, request.MissionId, request.AssignmentId, isolated, request.BaseRevision));
    }

    public ValueTask<string> InspectDiffAsync(WorkspaceLease workspace, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult($"Workspace {workspace.Path} is {(workspace.IsIsolated ? "isolated" : "shared")}; diff inspection requires the host Git adapter.");

    public ValueTask IntegrateAsync(WorkspaceLease workspace, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Repository integration requires an explicit host Git adapter and policy approval.");

    public ValueTask CleanupAsync(WorkspaceLease workspace, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
