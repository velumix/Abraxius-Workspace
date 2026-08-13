using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Abraxius.Security;

namespace Abraxius.Design;

public readonly record struct DesignSurfaceId(string Value)
{
    public static readonly DesignSurfaceId ChatWorkspace = new("chat.workspace");
    public static readonly DesignSurfaceId MissionWorkspace = new("mission.workspace");
    public override string ToString() => Value;
}

public readonly record struct DesignRequestId(Guid Value) { public static DesignRequestId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct DesignSessionId(Guid Value) { public static DesignSessionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct DesignGenerationId(Guid Value) { public static DesignGenerationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct DesignCandidateId(Guid Value) { public static DesignCandidateId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct DesignSourceSnapshotId(Guid Value) { public static DesignSourceSnapshotId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct DesignProjectId(string Value) { public override string ToString() => Value; }

public enum DesignSurfaceCategory { Shell, Workspace, Component, System }
public enum DesignCaptureMode { SyntheticContent, RedactedContent, LiveContent }
public enum DesignCaptureStatus { Captured, Unavailable, Failed }
public enum DesignSessionState { Capturing, Compiling, Generating, Ready, Refining, Implementing, Verifying, Completed, Cancelled, Failed }
public enum DesignProviderConnectionState { Disconnected, Connecting, Connected, Refreshing, Degraded, Failed, NeedsConfiguration }
public enum DesignAutomationLevel { Manual, Assisted, Automatic }
public enum DesignViewportProfile { Compact, Medium, Expanded, UltraWide }
public enum DesignConstraintKind { RequiredInteraction, RequiredElement, ForbiddenPattern, Accessibility, Responsive, Technology, Security, Performance }

public sealed record DesignSurfaceDescriptor(
    DesignSurfaceId Id,
    string DisplayName,
    DesignSurfaceCategory Category,
    ImmutableArray<string> RelevantFiles,
    ImmutableArray<string> RequiredInteractions,
    ImmutableArray<string> RequiredElements,
    ImmutableArray<string> ResponsiveProfiles,
    ImmutableArray<string> Constraints)
{
    public ImmutableArray<string> SafeRelevantFiles => RelevantFiles.IsDefault ? [] : RelevantFiles;
    public ImmutableArray<string> SafeRequiredInteractions => RequiredInteractions.IsDefault ? [] : RequiredInteractions;
    public ImmutableArray<string> SafeRequiredElements => RequiredElements.IsDefault ? [] : RequiredElements;
    public ImmutableArray<string> SafeResponsiveProfiles => ResponsiveProfiles.IsDefault ? [] : ResponsiveProfiles;
    public ImmutableArray<string> SafeConstraints => Constraints.IsDefault ? [] : Constraints;
}

public sealed record DesignCaptureRequest(
    DesignViewportProfile Profile,
    int Width,
    int Height,
    double Scale = 1,
    DesignCaptureMode Mode = DesignCaptureMode.SyntheticContent,
    bool IncludeScreenshot = true);

public sealed record DesignSurfaceSnapshot(
    DesignSurfaceId Surface,
    DesignCaptureRequest Request,
    DesignCaptureStatus Status,
    byte[]? ScreenshotPng,
    string? FailureReason,
    DateTimeOffset CapturedAt,
    string ContentIdentity)
{
    public bool HasScreenshot => ScreenshotPng is { Length: > 0 };
}

public interface IDesignableSurface
{
    DesignSurfaceId Id { get; }
    string DisplayName { get; }
    DesignSurfaceCategory Category { get; }
    DesignSurfaceDescriptor Describe();
    ValueTask<DesignSurfaceSnapshot> CaptureAsync(DesignCaptureRequest request, CancellationToken cancellationToken = default);
}

public sealed class DesignableSurface : IDesignableSurface
{
    private readonly DesignSurfaceDescriptor _descriptor;
    private Func<DesignCaptureRequest, CancellationToken, ValueTask<DesignSurfaceSnapshot>> _capture;
    public DesignableSurface(DesignSurfaceDescriptor descriptor, Func<DesignCaptureRequest, CancellationToken, ValueTask<DesignSurfaceSnapshot>>? capture = null)
    {
        _descriptor = descriptor;
        _capture = capture ?? UnavailableAsync;
    }
    public DesignSurfaceId Id => _descriptor.Id;
    public string DisplayName => _descriptor.DisplayName;
    public DesignSurfaceCategory Category => _descriptor.Category;
    public DesignSurfaceDescriptor Describe() => _descriptor;
    public ValueTask<DesignSurfaceSnapshot> CaptureAsync(DesignCaptureRequest request, CancellationToken cancellationToken = default) => _capture(request, cancellationToken);
    public void AttachCapture(Func<DesignCaptureRequest, CancellationToken, ValueTask<DesignSurfaceSnapshot>> capture) =>
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));

    private ValueTask<DesignSurfaceSnapshot> UnavailableAsync(DesignCaptureRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DesignSurfaceSnapshot(Id, request, DesignCaptureStatus.Unavailable, null,
            "No live Avalonia capture adapter is registered for this surface.", DateTimeOffset.UtcNow, "unavailable"));
}

public interface IDesignSurfaceRegistry
{
    void Register(IDesignableSurface surface);
    bool AttachCapture(DesignSurfaceId id, Func<DesignCaptureRequest, CancellationToken, ValueTask<DesignSurfaceSnapshot>> capture);
    IDesignableSurface Resolve(DesignSurfaceId id);
    IReadOnlyList<IDesignableSurface> List();
}

public sealed class DesignSurfaceRegistry : IDesignSurfaceRegistry
{
    private readonly ConcurrentDictionary<DesignSurfaceId, IDesignableSurface> _surfaces = new();
    public void Register(IDesignableSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!_surfaces.TryAdd(surface.Id, surface)) throw new InvalidOperationException($"Design surface '{surface.Id}' is already registered.");
    }
    public bool AttachCapture(DesignSurfaceId id, Func<DesignCaptureRequest, CancellationToken, ValueTask<DesignSurfaceSnapshot>> capture)
    {
        if (!_surfaces.TryGetValue(id, out var surface) || surface is not DesignableSurface designable) return false;
        designable.AttachCapture(capture);
        return true;
    }
    public IDesignableSurface Resolve(DesignSurfaceId id) => _surfaces.TryGetValue(id, out var surface) ? surface : throw new KeyNotFoundException($"Design surface '{id}' is not registered.");
    public IReadOnlyList<IDesignableSurface> List() => _surfaces.Values.OrderBy(static item => item.Category).ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record DesignSourceFile(string RelativePath, string ContentHash, long Length, string Content);
public sealed record DesignSourceSnapshot(
    DesignSourceSnapshotId Id,
    string? GitCommit,
    string Branch,
    DesignSurfaceId Surface,
    ImmutableArray<DesignSourceFile> Files,
    string ThemeHash,
    DateTimeOffset CreatedAt)
{
    public ImmutableArray<DesignSourceFile> SafeFiles => Files.IsDefault ? [] : Files;
    public string SnapshotHash => ComputeHash();
    private string ComputeHash()
    {
        var input = string.Join('\n', SafeFiles.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).Select(static item => $"{item.RelativePath}:{item.ContentHash}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Surface}|{Branch}|{GitCommit}|{ThemeHash}|{input}"))).ToLowerInvariant();
    }
}

public interface IDesignSourceResolver
{
    ValueTask<DesignSourceSnapshot> ResolveAsync(DesignSurfaceDescriptor surface, CancellationToken cancellationToken = default);
}

public sealed class FileSystemDesignSourceResolver(string workspaceRoot, string branch = "dev", string? gitCommit = null) : IDesignSourceResolver
{
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot);
    public async ValueTask<DesignSourceSnapshot> ResolveAsync(DesignSurfaceDescriptor surface, CancellationToken cancellationToken = default)
    {
        var files = ImmutableArray.CreateBuilder<DesignSourceFile>();
        foreach (var relative in surface.SafeRelevantFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_workspaceRoot, normalized));
            if (!full.StartsWith(_workspaceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, _workspaceRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(full)) continue;
            var content = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            files.Add(new DesignSourceFile(relative.Replace('\\', '/'), hash, Encoding.UTF8.GetByteCount(content), content.Length > 20_000 ? content[..20_000] + "\n/* truncated */" : content));
        }
        var theme = string.Join('|', files.Where(static item => item.RelativePath.Contains("theme", StringComparison.OrdinalIgnoreCase) || item.RelativePath.Contains("style", StringComparison.OrdinalIgnoreCase)).Select(static item => item.ContentHash));
        return new DesignSourceSnapshot(DesignSourceSnapshotId.New(), gitCommit, branch, surface.Id, files.ToImmutable(), theme, DateTimeOffset.UtcNow);
    }
}

public sealed record DesignConstraint(DesignConstraintKind Kind, string Text, bool Required = true);
public static class AbraxiusDesignPrinciples
{
    public static ImmutableArray<string> Default { get; } =
    [
        "Dark neutral workstation surfaces with restrained teal for active, live, and focused states.",
        "Readable conversation geometry; long text is a document, not a tiny bubble.",
        "Inter is the normal UI typeface; monospace is reserved for code and diagnostics.",
        "No purple AI aesthetic, decorative card trims, permanent neon glow, fake progress, or fake telemetry.",
        "Preserve explicit user intent, accessibility, keyboard parity, reduced motion, and responsive behavior.",
        "Translate design intent into native Avalonia controls and compiled bindings; generated HTML is reference only."
    ];
}

public sealed record DesignGenerationContext(
    DesignSurfaceDescriptor Surface,
    string Objective,
    DesignCaptureRequest CaptureRequest,
    DesignSurfaceSnapshot? Screenshot,
    DesignSourceSnapshot Source,
    ImmutableArray<DesignConstraint> Constraints,
    ImmutableArray<string> Principles,
    string Brief,
    DataClassification Classification)
{
    public ImmutableArray<DesignConstraint> SafeConstraints => Constraints.IsDefault ? [] : Constraints;
    public ImmutableArray<string> SafePrinciples => Principles.IsDefault ? [] : Principles;
}

public interface IDesignEgressPolicy
{
    AuthorizationDecision Evaluate(DataClassification classification, DesignProviderId provider, bool providerIsLocal = false);
}

public sealed class AllowDesignEgressPolicy : IDesignEgressPolicy
{
    public AuthorizationDecision Evaluate(DataClassification classification, DesignProviderId provider, bool providerIsLocal = false) =>
        providerIsLocal || classification is not (DataClassification.LocalOnly or DataClassification.Secret)
            ? new(AuthorizationDecisionId.New(), AuthorizationOutcome.Allow, AuthorizationReasonCode.AllowedByPolicy,
                "Design provider egress is allowed by the configured policy.", RiskClass.ReadOnly)
            : new(AuthorizationDecisionId.New(), AuthorizationOutcome.Deny, AuthorizationReasonCode.DeniedLocalOnlyPolicy,
                $"{classification} design context cannot be sent to provider '{provider}'.", RiskClass.ExternalSideEffect);
}

public interface IDesignContextCompiler
{
    ValueTask<DesignGenerationContext> CompileAsync(
        string objective,
        DesignSurfaceDescriptor surface,
        DesignSurfaceSnapshot? screenshot,
        DesignSourceSnapshot source,
        DesignCaptureRequest captureRequest,
        DataClassification classification,
        CancellationToken cancellationToken = default);
}

public sealed class DesignContextCompiler : IDesignContextCompiler
{
    public ValueTask<DesignGenerationContext> CompileAsync(string objective, DesignSurfaceDescriptor surface, DesignSurfaceSnapshot? screenshot, DesignSourceSnapshot source,
        DesignCaptureRequest captureRequest, DataClassification classification, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var constraints = surface.SafeConstraints.Select(text => new DesignConstraint(DesignConstraintKind.ForbiddenPattern, text)).Concat(
            surface.SafeRequiredInteractions.Select(text => new DesignConstraint(DesignConstraintKind.RequiredInteraction, text))).Concat(
            surface.SafeRequiredElements.Select(text => new DesignConstraint(DesignConstraintKind.RequiredElement, text))).Concat(
            surface.SafeResponsiveProfiles.Select(text => new DesignConstraint(DesignConstraintKind.Responsive, text))).ToImmutableArray();
        var builder = new StringBuilder();
        builder.AppendLine("Product: Abraxius");
        builder.AppendLine("Technology: Avalonia 12.1");
        builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "Surface: {0} ({1})", surface.Id, surface.DisplayName));
        builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "Objective: {0}", objective.Trim()));
        builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "Capture: {0}, {1}x{2}, {3}", captureRequest.Profile, captureRequest.Width, captureRequest.Height, captureRequest.Mode));
        builder.AppendLine("Visual principles:");
        foreach (var principle in AbraxiusDesignPrinciples.Default) builder.AppendLine(string.Concat("- ", principle));
        builder.AppendLine("Required behavior:");
        foreach (var constraint in constraints) builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "- [{0}] {1}", constraint.Kind, constraint.Text));
        builder.AppendLine("Relevant implementation source:");
        foreach (var file in source.SafeFiles)
        {
            builder.AppendLine(string.Format(CultureInfo.InvariantCulture, "--- {0} ({1})", file.RelativePath, file.ContentHash));
            builder.AppendLine(file.Content);
        }
        return ValueTask.FromResult(new DesignGenerationContext(surface, objective.Trim(), captureRequest, screenshot, source, constraints, AbraxiusDesignPrinciples.Default, builder.ToString(), classification));
    }
}

public readonly record struct DesignProviderId(string Value) { public override string ToString() => Value; }
public sealed record DesignProviderHealth(DesignProviderId Provider, DesignProviderConnectionState State, string Message, DateTimeOffset CheckedAt, bool CanGenerate);
public sealed record DesignProjectRequest(string StableKey, string Title);
public sealed record DesignProjectRef(DesignProviderId Provider, DesignProjectId ProjectId, string DisplayName, string? ProviderUri = null);
public sealed record DesignGenerationRequest(DesignGenerationId GenerationId, DesignGenerationContext Context, DesignProjectRef Project, int VariantCount = 3, string? Strategy = null);
public sealed record DesignVariantRequest(DesignGenerationId GenerationId, DesignGenerationContext Context, DesignProjectRef Project, DesignCandidate BaseCandidate, int VariantCount = 3, string? Strategy = null);
public sealed record DesignRefinementRequest(DesignGenerationId GenerationId, DesignGenerationContext Context, DesignProjectRef Project, DesignCandidate BaseCandidate, ImmutableArray<DesignCandidate> References, string Instruction);

public sealed record DesignCandidate(
    DesignCandidateId Id,
    string Title,
    string? ProviderScreenRef,
    string? GeneratedMarkup,
    byte[]? ScreenshotPng,
    string Prompt,
    DesignSourceSnapshot SourceSnapshot,
    DesignCaptureRequest Viewport,
    ImmutableDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    DesignCandidateId? DerivedFrom = null,
    ImmutableArray<DesignCandidateId> References = default,
    string? ArtifactReference = null)
{
    public ImmutableDictionary<string, string> SafeMetadata => Metadata ?? ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<DesignCandidateId> SafeReferences => References.IsDefault ? [] : References;
}

public sealed record DesignGenerationResult(DesignGenerationId GenerationId, DesignProviderId Provider, DesignProjectRef Project, ImmutableArray<DesignCandidate> Candidates,
    string PromptSnapshot, DesignSourceSnapshot SourceSnapshot, ImmutableDictionary<string, string> ProviderMetadata, TimeSpan Duration)
{
    public ImmutableArray<DesignCandidate> SafeCandidates => Candidates.IsDefault ? [] : Candidates;
}

public sealed record DesignCandidateArtifactReference(string Reference, string? RevisionReference = null);
public interface IDesignArtifactSink
{
    ValueTask<DesignCandidateArtifactReference> PersistCandidateAsync(DesignGenerationResult generation, DesignCandidate candidate, CancellationToken cancellationToken = default);
}

public interface IDesignGenerationProvider
{
    DesignProviderId Id { get; }
    ValueTask<DesignProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    ValueTask<DesignProjectRef> EnsureProjectAsync(DesignProjectRequest request, CancellationToken cancellationToken = default);
    ValueTask<DesignGenerationResult> GenerateAsync(DesignGenerationRequest request, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DesignCandidate>> GenerateVariantsAsync(DesignVariantRequest request, CancellationToken cancellationToken = default);
    ValueTask<DesignGenerationResult> RefineAsync(DesignRefinementRequest request, CancellationToken cancellationToken = default);
}

public sealed record DesignSession(
    DesignSessionId Id,
    DesignRequestId RequestId,
    DesignSurfaceId Surface,
    string Objective,
    DesignSessionState State,
    DesignGenerationResult? Generation,
    DesignCandidateId? SelectedCandidate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Error = null);

public sealed class DesignOrchestrator(
    IDesignSurfaceRegistry surfaces,
    IDesignSourceResolver sourceResolver,
    IDesignContextCompiler contextCompiler,
    IDesignGenerationProvider provider,
    IDesignProjectResolver projectResolver,
    IDesignEgressPolicy? egressPolicy = null,
    IDesignArtifactSink? artifactSink = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<DesignSessionId, DesignSession> _sessions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDesignEgressPolicy _egressPolicy = egressPolicy ?? new AllowDesignEgressPolicy();
    public IReadOnlyList<DesignSession> Sessions => _sessions.Values.OrderByDescending(static item => item.UpdatedAt).ToArray();
    public IDesignGenerationProvider Provider => provider;

    public async ValueTask<DesignSession> GenerateAsync(DesignSurfaceId surfaceId, string objective, DesignCaptureRequest captureRequest,
        DataClassification classification = DataClassification.Internal, int variantCount = 3, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("A design objective is required.", nameof(objective));
        var egress = _egressPolicy.Evaluate(classification, provider.Id);
        if (!egress.IsAllowed) throw new DesignProviderSecurityException(egress.HumanExplanation, egress.ReasonCode);
        var surface = surfaces.Resolve(surfaceId);
        var session = new DesignSession(DesignSessionId.New(), DesignRequestId.New(), surfaceId, objective.Trim(), DesignSessionState.Capturing, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _sessions[session.Id] = session;
        try
        {
            var screenshot = await surface.CaptureAsync(captureRequest, cancellationToken).ConfigureAwait(false);
            session = Update(session, DesignSessionState.Compiling);
            var source = await sourceResolver.ResolveAsync(surface.Describe(), cancellationToken).ConfigureAwait(false);
            var context = await contextCompiler.CompileAsync(objective, surface.Describe(), screenshot.Status == DesignCaptureStatus.Captured ? screenshot : null,
                source, captureRequest, classification, cancellationToken).ConfigureAwait(false);
            session = Update(session, DesignSessionState.Generating);
            var project = await projectResolver.ResolveAsync(new DesignProjectRequest("abraxius.workspace", "Abraxius Workspace"), cancellationToken).ConfigureAwait(false);
            var generation = await provider.GenerateAsync(new DesignGenerationRequest(DesignGenerationId.New(), context, project, Math.Clamp(variantCount, 1, 5)), cancellationToken).ConfigureAwait(false);
            var candidates = generation.SafeCandidates;
            if (artifactSink is not null)
            {
                var persisted = ImmutableArray.CreateBuilder<DesignCandidate>(candidates.Length);
                foreach (var candidate in candidates)
                {
                    var artifact = await artifactSink.PersistCandidateAsync(generation, candidate, cancellationToken).ConfigureAwait(false);
                    persisted.Add(candidate with { ArtifactReference = artifact.Reference });
                }
                generation = generation with { Candidates = persisted.ToImmutable() };
            }
            session = session with { State = DesignSessionState.Ready, Generation = generation, UpdatedAt = DateTimeOffset.UtcNow };
            _sessions[session.Id] = session;
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session = Update(session, DesignSessionState.Cancelled, "Design generation cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            session = Update(session, DesignSessionState.Failed, exception.Message);
            throw;
        }
    }

    public bool TrySelect(DesignSessionId sessionId, DesignCandidateId candidateId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.Generation?.SafeCandidates.All(item => item.Id != candidateId) != false) return false;
        _sessions[sessionId] = session with { SelectedCandidate = candidateId, UpdatedAt = DateTimeOffset.UtcNow };
        return true;
    }

    public bool TryGet(DesignSessionId id, out DesignSession? session) => _sessions.TryGetValue(id, out session);

    public async ValueTask<DesignSession> RefineAsync(DesignSessionId sessionId, DesignCandidateId baseCandidateId, string instruction, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.Generation is null) throw new KeyNotFoundException("Design session or generation not found.");
        var baseCandidate = session.Generation.SafeCandidates.Single(item => item.Id == baseCandidateId);
        var surface = surfaces.Resolve(session.Surface);
        var captureRequest = baseCandidate.Viewport;
        var screenshot = await surface.CaptureAsync(captureRequest, cancellationToken).ConfigureAwait(false);
        var source = await sourceResolver.ResolveAsync(surface.Describe(), cancellationToken).ConfigureAwait(false);
        var context = await contextCompiler.CompileAsync(instruction, surface.Describe(), screenshot.Status == DesignCaptureStatus.Captured ? screenshot : null, source, captureRequest, DataClassification.Internal, cancellationToken).ConfigureAwait(false);
        var project = session.Generation.Project;
        _sessions[sessionId] = Update(session, DesignSessionState.Refining);
        var result = await provider.RefineAsync(new DesignRefinementRequest(DesignGenerationId.New(), context, project, baseCandidate, session.Generation.SafeCandidates.Where(item => item.Id != baseCandidateId).ToImmutableArray(), instruction), cancellationToken).ConfigureAwait(false);
        var all = session.Generation.SafeCandidates.AddRange(result.SafeCandidates);
        var combined = session.Generation with { GenerationId = result.GenerationId, Candidates = all, SourceSnapshot = result.SourceSnapshot, PromptSnapshot = result.PromptSnapshot };
        if (artifactSink is not null)
        {
            var persisted = ImmutableArray.CreateBuilder<DesignCandidate>(result.SafeCandidates.Length);
            foreach (var candidate in result.SafeCandidates)
            {
                var artifact = await artifactSink.PersistCandidateAsync(result, candidate, cancellationToken).ConfigureAwait(false);
                persisted.Add(candidate with { ArtifactReference = artifact.Reference });
            }
            var refined = persisted.ToImmutable();
            all = session.Generation.SafeCandidates.AddRange(refined);
            combined = session.Generation with { GenerationId = result.GenerationId, Candidates = all, SourceSnapshot = result.SourceSnapshot, PromptSnapshot = result.PromptSnapshot };
        }
        var updated = session with { State = DesignSessionState.Ready, Generation = combined, UpdatedAt = DateTimeOffset.UtcNow };
        _sessions[sessionId] = updated;
        return updated;
    }

    private void Set(DesignSession session) => _sessions[session.Id] = session;
    private DesignSession Update(DesignSession session, DesignSessionState state, string? error = null)
    {
        var updated = session with { State = state, Error = error, UpdatedAt = DateTimeOffset.UtcNow };
        Set(updated);
        return updated;
    }
    public ValueTask DisposeAsync() { _lifetime.Cancel(); _lifetime.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class DesignProviderSecurityException(string message, AuthorizationReasonCode reasonCode) : UnauthorizedAccessException(message)
{
    public AuthorizationReasonCode ReasonCode { get; } = reasonCode;
}

public interface IDesignProjectResolver
{
    ValueTask<DesignProjectRef> ResolveAsync(DesignProjectRequest request, CancellationToken cancellationToken = default);
}

public sealed class StableDesignProjectResolver(IDesignGenerationProvider provider, string? configuredProjectId = null) : IDesignProjectResolver
{
    public ValueTask<DesignProjectRef> ResolveAsync(DesignProjectRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new DesignProjectRef(provider.Id, new DesignProjectId(configuredProjectId ?? $"local:{request.StableKey}"), request.Title));
}
