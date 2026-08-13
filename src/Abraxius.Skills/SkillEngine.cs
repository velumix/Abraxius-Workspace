using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Abraxius.Agents;
using Abraxius.Axl;
using Abraxius.Protocol;

namespace Abraxius.Skills;

public sealed record SkillRegistrySnapshot(IReadOnlyList<SkillDefinition> Skills, int SchemaVersion = 1)
{
    public IReadOnlyList<SkillDefinition> SafeSkills => Skills ?? Array.Empty<SkillDefinition>();
}

public interface ISkillRegistryStore
{
    ValueTask<SkillRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(SkillRegistrySnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class InMemorySkillRegistryStore : ISkillRegistryStore
{
    private SkillRegistrySnapshot _snapshot = new(Array.Empty<SkillDefinition>());
    public ValueTask<SkillRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_snapshot);
    public ValueTask SaveAsync(SkillRegistrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _snapshot = snapshot;
        return ValueTask.CompletedTask;
    }
}

public sealed class JsonSkillRegistryStore(string path) : ISkillRegistryStore
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static JsonSkillRegistryStore()
    {
        JsonOptions.Converters.Add(new SkillIdJsonConverter());
        JsonOptions.Converters.Add(new SkillVersionJsonConverter());
        JsonOptions.Converters.Add(new SkillStepIdJsonConverter());
    }

    public async ValueTask<SkillRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return new SkillRegistrySnapshot(Array.Empty<SkillDefinition>());
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<SkillRegistrySnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new SkillRegistrySnapshot(Array.Empty<SkillDefinition>());
    }

    public async ValueTask SaveAsync(SkillRegistrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot = new SkillRegistrySnapshot(snapshot.Skills.Select(Normalize).ToArray());
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _path, overwrite: true);
    }

    private static SkillDefinition Normalize(SkillDefinition skill) => skill with
    {
        Procedure = new SkillProcedure(skill.Procedure.SafeSteps.Select(static item => item switch
        {
            SkillContextQueryStep query => query with { Dependencies = query.SafeDependencies },
            SkillCapabilityCallStep call => call with { Dependencies = call.SafeDependencies },
            SkillSpecialistAssignmentStep assignment => assignment with { Dependencies = assignment.SafeDependencies },
            SkillVerificationStep verification => verification with { Dependencies = verification.SafeDependencies },
            SkillModelStep model => model with { Dependencies = model.SafeDependencies },
            SkillConditionalStep conditional => conditional with { Dependencies = conditional.SafeDependencies },
            SkillCompositionStep composition => composition with { Dependencies = composition.SafeDependencies },
            _ => item
        }).ToArray())
    };

    private sealed class SkillIdJsonConverter : JsonConverter<SkillId>
    {
        public override SkillId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? string.Empty);
        public override void Write(Utf8JsonWriter writer, SkillId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }

    private sealed class SkillVersionJsonConverter : JsonConverter<SkillVersion>
    {
        public override SkillVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => SkillVersion.TryParse(reader.GetString(), out var value) ? value : throw new JsonException("Invalid Skill version.");
        public override void Write(Utf8JsonWriter writer, SkillVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private sealed class SkillStepIdJsonConverter : JsonConverter<SkillStepId>
    {
        public override SkillStepId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? string.Empty);
        public override void Write(Utf8JsonWriter writer, SkillStepId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}

public sealed class SkillRegistry(ISkillRegistryStore? store = null, SkillEngineOptions? options = null) : ISkillRegistry
{
    private readonly ConcurrentDictionary<(SkillId Id, SkillVersion Version), SkillDefinition> _skills = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(SkillId Id, SkillVersion Version), byte>> _metadataIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISkillRegistryStore? _store = store;
    private readonly SkillEngineOptions _options = options ?? new SkillEngineOptions();
    private int _loaded;

    public IReadOnlyList<SkillDefinition> List(bool includeDisabled = false) => _skills.Values
        .Where(skill => includeDisabled || (skill.Enabled && skill.State != SkillLifecycleState.Disabled))
        .OrderBy(skill => skill.Id.Value, StringComparer.Ordinal)
        .ThenByDescending(static skill => skill.Version)
        .Take(_options.MaximumPersistedSkills)
        .ToArray();

    public bool TryGet(SkillId id, SkillVersion? version, out SkillDefinition skill)
    {
        if (version is { } selected)
        {
            return _skills.TryGetValue((id, selected), out skill!);
        }

        var candidate = _skills.Values.Where(item => item.Id == id && item.Enabled && item.State != SkillLifecycleState.Disabled)
            .OrderByDescending(static item => item.Version)
            .FirstOrDefault();
        skill = candidate!;
        return candidate is not null;
    }

    public IReadOnlyList<SkillDefinition> GetVersions(SkillId id) => _skills.Values.Where(item => item.Id == id).OrderByDescending(static item => item.Version).ToArray();

    public void Register(SkillDefinition skill, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var key = (skill.Id, skill.Version);
        if (!replace && !_skills.TryAdd(key, skill)) throw new InvalidOperationException($"Skill {skill.Id}/{skill.Version} is already registered.");
        if (replace)
        {
            if (_skills.TryGetValue(key, out var previous)) RemoveFromIndex(key, previous);
            _skills[key] = skill;
        }
        AddToIndex(key, skill);
    }

    public bool TryUpdate(SkillId id, SkillVersion version, Func<SkillDefinition, SkillDefinition> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        while (_skills.TryGetValue((id, version), out var current))
        {
            var next = update(current) ?? throw new InvalidOperationException("Skill update returned null.");
            if (_skills.TryUpdate((id, version), next, current))
            {
                RemoveFromIndex((id, version), current);
                AddToIndex((id, version), next);
                return true;
            }
        }

        return false;
    }

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _loaded, 1) != 0 || _store is null) return;
        foreach (var skill in (await _store.LoadAsync(cancellationToken).ConfigureAwait(false)).SafeSkills)
        {
            var key = (skill.Id, skill.Version);
            if (_skills.TryAdd(key, skill)) AddToIndex(key, skill);
        }
    }

    public ValueTask SaveAsync(CancellationToken cancellationToken = default) => _store is null
        ? ValueTask.CompletedTask
        : _store.SaveAsync(new SkillRegistrySnapshot(List(includeDisabled: true)), cancellationToken);

    public IReadOnlyList<SkillDefinition> FindIndexedCandidates(string objective)
    {
        var tokens = Tokenize(objective);
        if (tokens.Length == 0) return Array.Empty<SkillDefinition>();
        var postingLists = tokens
            .Select(token => _metadataIndex.TryGetValue(token, out var matches) ? matches : null)
            .Where(static matches => matches is not null)
            .Cast<ConcurrentDictionary<(SkillId Id, SkillVersion Version), byte>>()
            .OrderBy(static matches => matches.Count)
            .ToArray();
        if (postingLists.Length == 0) return Array.Empty<SkillDefinition>();
        // Start from the rarest indexed token. This keeps exact/technical queries
        // selective while the matcher still applies the complete score afterwards.
        var keys = postingLists[0].Keys.ToHashSet();
        return keys.Select(key => _skills.TryGetValue(key, out var skill) ? skill : null)
            .Where(static skill => skill is not null)
            .Select(static skill => skill!)
            .ToArray();
    }

    private void AddToIndex((SkillId Id, SkillVersion Version) key, SkillDefinition skill)
    {
        foreach (var token in MetadataTokens(skill))
        {
            _metadataIndex.GetOrAdd(token, static _ => new ConcurrentDictionary<(SkillId Id, SkillVersion Version), byte>())[key] = 0;
        }
    }

    private void RemoveFromIndex((SkillId Id, SkillVersion Version) key, SkillDefinition skill)
    {
        foreach (var token in MetadataTokens(skill))
        {
            if (_metadataIndex.TryGetValue(token, out var matches)) matches.TryRemove(key, out _);
        }
    }

    private static string[] MetadataTokens(SkillDefinition skill) => Tokenize(string.Join(' ',
        skill.Id.Value,
        skill.Name,
        string.Join(' ', skill.Triggers.SafeTaskClasses),
        string.Join(' ', skill.Triggers.SafeConcepts),
        string.Join(' ', skill.Triggers.SafeErrorCodes),
        string.Join(' ', skill.Triggers.SafeSymbols),
        string.Join(' ', skill.Triggers.SafeProjectTypes),
        string.Join(' ', skill.Triggers.SafeFrameworks)));

    internal static string[] Tokenize(string? value) => (value ?? string.Empty)
        .Split([' ', '\t', '\r', '\n', '.', '/', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static token => token.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

public sealed class DeterministicSkillMatcher(ISkillRegistry registry) : ISkillMatcher
{
    private readonly ISkillRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public IReadOnlyList<SkillMatch> Match(SkillMatchRequest request, int limit = 8)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.AllowSkillReuse && !request.ExplicitSkillRequest) return Array.Empty<SkillMatch>();
        var objective = request.Objective.Trim();
        var words = SkillRegistry.Tokenize(objective);
        var candidates = _registry is SkillRegistry indexed ? indexed.FindIndexedCandidates(objective) : _registry.List();
        var matches = new List<SkillMatch>();
        foreach (var skill in candidates)
        {
            if (!IsEligible(skill, request, out var rejection))
            {
                if (request.ExplicitSkillRequest && rejection is not null) matches.Add(new SkillMatch(skill, 0, [new SkillMatchReason("ineligible", rejection, 0)]));
                continue;
            }

            var reasons = new List<SkillMatchReason>();
            var score = 0d;
            var exact = ExactMatches(words, skill.Triggers.SafeConcepts.Concat(skill.Triggers.SafeTaskClasses).Concat(skill.Triggers.SafeErrorCodes).Concat(skill.Triggers.SafeSymbols));
            if (exact > 0)
            {
                var weight = Math.Min(0.62, 0.25 + exact * 0.12);
                score += weight;
                reasons.Add(new SkillMatchReason("trigger.exact", $"{exact} structured trigger(s) match the objective", weight));
            }

            var lexical = skill.Name.Replace('.', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(part => words.Any(word => string.Equals(word, part, StringComparison.OrdinalIgnoreCase)));
            if (lexical > 0)
            {
                var weight = Math.Min(0.24, lexical * 0.08);
                score += weight;
                reasons.Add(new SkillMatchReason("name.match", "skill name matches the requested task", weight));
            }

            if (skill.SpecialistPolicy.PreferredRole == request.SpecialistRole && request.SpecialistRole is not null)
            {
                score += 0.10;
                reasons.Add(new SkillMatchReason("role.match", $"preferred specialist role is {request.SpecialistRole}", 0.10));
            }

            if (skill.Triggers.SafeFrameworks.Any(framework => string.Equals(framework, request.Framework, StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.10;
                reasons.Add(new SkillMatchReason("framework.match", $"framework {request.Framework} matches", 0.10));
            }

            if (skill.Preconditions.Scope?.Kind == SkillScopeKind.Project) reasons.Add(new SkillMatchReason("scope.project", "project-scoped procedure matched", 0.04));
            if (skill.State == SkillLifecycleState.Trusted) score += 0.08;
            else if (skill.State == SkillLifecycleState.Validated) score += 0.05;
            else score += 0.02;
            reasons.Add(new SkillMatchReason("reliability", $"smoothed verified reliability {skill.Statistics.Reliability:P0}", skill.Statistics.Reliability * 0.08));
            score += skill.Statistics.Reliability * 0.08;
            matches.Add(new SkillMatch(skill, Math.Min(1, score), reasons));
        }

        return matches.OrderByDescending(static match => match.Score).ThenBy(static match => match.Skill.Id.Value, StringComparer.Ordinal).Take(Math.Max(1, limit)).ToArray();
    }

    private static bool IsEligible(SkillDefinition skill, SkillMatchRequest request, out string? rejection)
    {
        rejection = null;
        if (!skill.Enabled || skill.State is SkillLifecycleState.Candidate or SkillLifecycleState.Rejected or SkillLifecycleState.Disabled or SkillLifecycleState.Deprecated or SkillLifecycleState.NeedsRevalidation)
        {
            rejection = $"skill state is {skill.State}";
            return request.ExplicitSkillRequest && skill.State is not SkillLifecycleState.Rejected;
        }

        if (!skill.SpecialistPolicy.Allows(request.SpecialistRole)) { rejection = "specialist role is not allowed"; return false; }
        if (skill.CapabilityPolicy.Safety != SkillSafetyClass.ReadOnly && !request.AllowMutation) { rejection = "mutation policy is not enabled"; return false; }
        if (request.AvailableCapabilities is not null && !skill.Preconditions.SafeCapabilities.Concat(skill.CapabilityPolicy.SafeCapabilities).All(request.AvailableCapabilities.Contains)) { rejection = "required capability is unavailable"; return false; }
        if (skill.Preconditions.Scope is { } scope && !scope.Matches(request.ProjectKey, request.Language, request.Framework, request.RepositoryKey)) { rejection = "scope does not match"; return false; }
        if (skill.Preconditions.Language is not null && !string.Equals(skill.Preconditions.Language, request.Language, StringComparison.OrdinalIgnoreCase)) { rejection = "language does not match"; return false; }
        if (skill.Preconditions.Framework is not null && !string.Equals(skill.Preconditions.Framework, request.Framework, StringComparison.OrdinalIgnoreCase)) { rejection = "framework does not match"; return false; }
        return true;
    }

    private static int ExactMatches(IEnumerable<string> words, IEnumerable<string> triggers)
    {
        var set = words.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return triggers.Count(trigger => set.Contains(trigger) || trigger.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries).All(set.Contains));
    }
}

public sealed class SkillValidator : ISkillValidator
{
    public SkillValidationReport Validate(SkillDefinition skill, SkillValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(skill);
        options ??= new SkillValidationOptions();
        var diagnostics = new List<SkillDiagnostic>();
        if (!SkillId.TryParse(skill.Id.Value, out _)) diagnostics.Add(Error("SKILL001", "Skill ID is not a valid namespaced identifier."));
        if (string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Description)) diagnostics.Add(Error("SKILL002", "Skill name and description are required."));
        if (skill.Procedure.SafeSteps.Count == 0) diagnostics.Add(Error("SKILL003", "Skill procedure must contain at least one step."));
        if (skill.Procedure.SafeSteps.Count > Math.Min(options.MaxSteps, skill.ResourceProfile.MaxSteps)) diagnostics.Add(Error("SKILL004", "Skill procedure exceeds the configured step limit."));
        if (skill.CapabilityPolicy.Safety != SkillSafetyClass.ReadOnly && !options.AllowMutation) diagnostics.Add(Error("SKILL005", "Mutation-capable Skill is not allowed by the current validation policy.", SkillValidationStage.Policy));
        if (skill.CapabilityPolicy.Safety == SkillSafetyClass.ExternalSideEffect && !options.AllowExternalSideEffects) diagnostics.Add(Error("SKILL006", "External side effects are disabled by the current validation policy.", SkillValidationStage.Safety));
        if (skill.Origin is SkillOrigin.Imported or SkillOrigin.GeneratedCandidate && skill.State is SkillLifecycleState.Trusted) diagnostics.Add(Error("SKILL007", "Imported/generated Skills cannot enter Trusted state directly.", SkillValidationStage.Policy));
        if (skill.State == SkillLifecycleState.Experimental && skill.CapabilityPolicy.Safety == SkillSafetyClass.Mutation && options.RequireIsolationForExperimentalMutation && !skill.CapabilityPolicy.RequiresIsolation) diagnostics.Add(Error("SKILL008", "Experimental mutation Skills must require workspace isolation.", SkillValidationStage.Safety));
        if (skill.Verification.SafeCriteria.Count == 0) diagnostics.Add(Warning("SKILL009", "Skill has no explicit verification criteria; execution verification will be inconclusive."));

        var steps = skill.Procedure.SafeSteps;
        var ids = new HashSet<SkillStepId>();
        foreach (var step in steps)
        {
            if (!ids.Add(step.Id)) diagnostics.Add(Error("SKILL010", $"Duplicate step ID '{step.Id}'.", SkillValidationStage.Structural, step.Id.Value));
            foreach (var dependency in step.SafeDependencies)
            {
                if (!steps.Any(candidate => candidate.Id == dependency)) diagnostics.Add(Error("SKILL011", $"Step '{step.Id}' depends on unknown step '{dependency}'.", SkillValidationStage.Structural, step.Id.Value));
            }

            if (step is SkillCapabilityCallStep call && !options.AllowMutation && call.Mutation) diagnostics.Add(Error("SKILL012", $"Mutation capability call '{call.Capability}' is not allowed.", SkillValidationStage.Policy, step.Id.Value));
            if (step is SkillConditionalStep conditional && conditional.ThenStep == conditional.Id) diagnostics.Add(Error("SKILL013", "A conditional step cannot select itself.", SkillValidationStage.Structural, step.Id.Value));
            if (step is SkillCompositionStep composition && composition.ChildSkill == skill.Id) diagnostics.Add(Error("SKILL022", "A Skill cannot compose itself.", SkillValidationStage.Structural, step.Id.Value));
        }

        var plan = SkillPlanCompiler.Compile(skill);
        diagnostics.AddRange(plan.Diagnostics);
        var axlReport = SkillAxlProjection.ValidateAxlProjection(skill);
        diagnostics.AddRange(axlReport.Diagnostics);
        if (options.AvailableCapabilities is not null)
        {
            foreach (var capability in skill.Preconditions.SafeCapabilities.Concat(skill.CapabilityPolicy.SafeCapabilities))
            {
                if (!options.AvailableCapabilities.Contains(capability)) diagnostics.Add(Error("SKILL014", $"Required capability '{capability}' is not available.", SkillValidationStage.Capability));
            }
        }

        return new SkillValidationReport(!diagnostics.Any(static item => item.Severity == SkillDiagnosticSeverity.Error), diagnostics, SkillValidationId.New(), DateTimeOffset.UtcNow);
    }

    private static SkillDiagnostic Error(string code, string message, SkillValidationStage stage = SkillValidationStage.Structural, string? step = null) => new(SkillDiagnosticSeverity.Error, code, message, step, stage);
    private static SkillDiagnostic Warning(string code, string message) => new(SkillDiagnosticSeverity.Warning, code, message);
}

public static class SkillPlanCompiler
{
    public static SkillExecutionPlan Compile(SkillDefinition skill)
    {
        var diagnostics = new List<SkillDiagnostic>();
        var steps = skill.Procedure.SafeSteps;
        var byId = steps.ToDictionary(static step => step.Id);
        var levels = new List<IReadOnlyList<SkillStep>>();
        var remaining = steps.ToDictionary(static step => step.Id, static step => step);
        var completed = new HashSet<SkillStepId>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Values.Where(step => step.SafeDependencies.All(completed.Contains)).ToArray();
            if (ready.Length == 0)
            {
                diagnostics.Add(new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL015", "Skill procedure contains a dependency cycle.", Stage: SkillValidationStage.Structural));
                break;
            }

            levels.Add(ready);
            foreach (var step in ready)
            {
                remaining.Remove(step.Id);
                completed.Add(step.Id);
            }
        }

        foreach (var step in steps.OfType<SkillConditionalStep>())
        {
            if (!byId.ContainsKey(step.ThenStep) || (step.ElseStep is { } elseStep && !byId.ContainsKey(elseStep))) diagnostics.Add(new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL016", "Conditional branch references an unknown step.", step.Id.Value, SkillValidationStage.Structural));
        }

        return new SkillExecutionPlan(levels, diagnostics);
    }
}

public sealed class SkillExecutor(
    ISkillValidator validator,
    ISkillStepRunner runner,
    SkillEngineOptions? options = null,
    ISkillRegistry? registry = null) : ISkillExecutor
{
    private readonly SkillEngineOptions _options = options ?? new SkillEngineOptions();
    private readonly ISkillRegistry? _registry = registry;

    public async ValueTask<SkillExecutionResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        var skill = request.Skill;
        var validation = validator.Validate(skill, new SkillValidationOptions(
            AllowMutation: request.AllowMutation,
            AllowExternalSideEffects: request.AllowExternalSideEffects));
        if (!validation.IsValid) return Failure(skill, SkillExecutionStatus.Blocked, "Skill failed validation.", "ValidationFailed", started, validation.Diagnostics);
        var compositionKey = $"{skill.Id}/{skill.Version}";
        var compositionStack = request.CompositionStack is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(request.CompositionStack, StringComparer.OrdinalIgnoreCase);
        if (!compositionStack.Add(compositionKey)) return Failure(skill, SkillExecutionStatus.Blocked, "Skill composition cycle detected.", "CompositionCycle", started, [new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL023", $"Skill '{compositionKey}' is already active.", Stage: SkillValidationStage.Structural)]);
        if (compositionStack.Count > _options.MaximumCompositionDepth) return Failure(skill, SkillExecutionStatus.Blocked, "Skill composition depth exceeded the configured limit.", "CompositionDepthExceeded", started, [new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL024", $"Maximum Skill composition depth is {_options.MaximumCompositionDepth}.", Stage: SkillValidationStage.Structural)]);
        var requestDiagnostics = ValidateExecutionRequest(request);
        if (requestDiagnostics.Count > 0) return Failure(skill, SkillExecutionStatus.Blocked, "Skill preconditions were not satisfied.", "PreconditionFailed", started, requestDiagnostics);
        if (request.DryRun) return new(skill.Id, skill.Version, SkillExecutionId.New(), SkillExecutionStatus.DryRun, "Skill dry-run compiled successfully.", null, null, SkillVerificationOutcome.NotRun, Stopwatch.GetElapsedTime(started), Diagnostics: validation.Diagnostics);

        var executionId = SkillExecutionId.New();
        var plan = SkillPlanCompiler.Compile(skill);
        var inputs = request.Inputs ?? ImmutableDictionary<string, SkillParameterValue>.Empty;
        var results = new ConcurrentDictionary<SkillStepId, SkillStepResult>();
        using var timeout = skill.ResourceProfile.MaximumDuration is { } duration ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        timeout?.CancelAfter(skill.ResourceProfile.MaximumDuration!.Value);
        var token = timeout?.Token ?? cancellationToken;
        try
        {
            foreach (var level in plan.Levels)
            {
                token.ThrowIfCancellationRequested();
                using var limiter = new SemaphoreSlim(Math.Max(1, Math.Min(skill.ResourceProfile.MaxConcurrentSteps, skill.SpecialistPolicy.MaxParallelSpecialists)));
                var tasks = level.Select(async step =>
                {
                    await limiter.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        var context = new SkillExecutionContext(skill, executionId, request.MissionId, request.SpecialistRole, inputs, request.ProjectKey, request.WorkspacePath, results, token);
                        var result = await RunStepAsync(step, request, context, compositionStack).ConfigureAwait(false);
                        results[step.Id] = result;
                    }
                    finally { limiter.Release(); }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
                var failed = level.Select(step => results[step.Id]).FirstOrDefault(static result => !result.Succeeded);
                if (failed is not null) return new(skill.Id, skill.Version, executionId, SkillExecutionStatus.Failed, failed.Summary, failed.SafeOutputs, failed.SafeEvidence, failed.Verification, Stopwatch.GetElapsedTime(started), FailureCode: failed.FailureCode);
            }

            var allEvidence = results.Values.SelectMany(static result => result.SafeEvidence).Distinct().ToArray();
            var verification = results.Values.Any(static result => result.Verification == SkillVerificationOutcome.Failed) ? SkillVerificationOutcome.Failed : results.Values.Any(static result => result.Verification == SkillVerificationOutcome.Inconclusive) ? SkillVerificationOutcome.Inconclusive : skill.Verification.SafeCriteria.Count == 0 ? SkillVerificationOutcome.NotRun : SkillVerificationOutcome.Passed;
            var status = request.RequireVerification && verification == SkillVerificationOutcome.Inconclusive ? SkillExecutionStatus.Blocked : verification == SkillVerificationOutcome.Failed ? SkillExecutionStatus.Failed : SkillExecutionStatus.Succeeded;
            return new(skill.Id, skill.Version, executionId, status, status == SkillExecutionStatus.Succeeded ? "Skill procedure completed and passed its verification boundary." : "Skill procedure completed without sufficient verification.", null, allEvidence, verification, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException)
        {
            return new(skill.Id, skill.Version, executionId, SkillExecutionStatus.Cancelled, "Skill execution cancelled.", null, results.Values.SelectMany(static result => result.SafeEvidence).Distinct().ToArray(), SkillVerificationOutcome.Inconclusive, Stopwatch.GetElapsedTime(started), FailureCode: "Cancelled");
        }
    }

    private async ValueTask<SkillStepResult> RunStepAsync(
        SkillStep step,
        SkillExecutionRequest request,
        SkillExecutionContext context,
        IReadOnlySet<string> compositionStack)
    {
        if (step is not SkillCompositionStep composition)
        {
            return await runner.RunAsync(step, context).ConfigureAwait(false);
        }

        if (_registry is null) return new SkillStepResult(false, "Skill composition requires a registry.", FailureCode: "CompositionRegistryUnavailable");
        if (!_registry.TryGet(composition.ChildSkill, composition.ChildVersion, out var child)) return new SkillStepResult(false, $"Composed Skill '{composition.ChildSkill}/{composition.ChildVersion?.ToString() ?? "current"}' is unavailable.", FailureCode: "ComposedSkillUnavailable");
        if (!child.Enabled || child.State is SkillLifecycleState.Disabled or SkillLifecycleState.Rejected or SkillLifecycleState.Deprecated or SkillLifecycleState.NeedsRevalidation)
        {
            return new SkillStepResult(false, $"Composed Skill '{child.Id}/{child.Version}' is not eligible in state {child.State}.", FailureCode: "ComposedSkillIneligible");
        }

        var childInputs = new Dictionary<string, SkillParameterValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in composition.SafeInputBindings)
        {
            if (!context.Inputs.TryGetValue(binding.Value, out var value)) return new SkillStepResult(false, $"Composition input '{binding.Value}' is unavailable.", FailureCode: "CompositionInputUnavailable");
            childInputs[binding.Key] = value;
        }

        var childResult = await ExecuteAsync(new SkillExecutionRequest(
            child,
            childInputs,
            request.MissionId,
            request.SpecialistRole,
            request.ProjectKey,
            request.WorkspacePath,
            DryRun: false,
            RequireVerification: request.RequireVerification,
            ExplicitVersion: true,
            AllowMutation: request.AllowMutation && context.Skill.CapabilityPolicy.Safety != SkillSafetyClass.ReadOnly,
            AllowExternalSideEffects: request.AllowExternalSideEffects && context.Skill.CapabilityPolicy.Safety == SkillSafetyClass.ExternalSideEffect,
            CompositionStack: compositionStack), context.CancellationToken).ConfigureAwait(false);
        return new SkillStepResult(
            childResult.Succeeded,
            $"{composition.Label}: {childResult.Summary}",
            childResult.SafeOutputs,
            childResult.SafeEvidence,
            childResult.Verification,
            childResult.FailureCode ?? (childResult.Succeeded ? null : "ComposedSkillFailed"));
    }

    private static List<SkillDiagnostic> ValidateExecutionRequest(SkillExecutionRequest request)
    {
        var diagnostics = new List<SkillDiagnostic>();
        var values = new Dictionary<string, SkillParameterValue>(StringComparer.OrdinalIgnoreCase);
        if (request.Inputs is not null)
        {
            foreach (var pair in request.Inputs) values[pair.Key] = pair.Value;
        }

        foreach (var parameter in request.Skill.Inputs.SafeParameters)
        {
            if (!values.TryGetValue(parameter.Name, out var value))
            {
                if (parameter.DefaultValue is not null) continue;
                if (parameter.Required) diagnostics.Add(new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL017", $"Required input '{parameter.Name}' is missing.", Stage: SkillValidationStage.Structural));
                continue;
            }

            var expected = parameter.Type switch
            {
                SkillValueType.Text => SkillParameterKind.Text,
                SkillValueType.WholeNumber => SkillParameterKind.WholeNumber,
                SkillValueType.Boolean => SkillParameterKind.Boolean,
                SkillValueType.Duration => SkillParameterKind.Duration,
                _ => SkillParameterKind.Reference
            };
            if (value.Kind != expected) diagnostics.Add(new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL018", $"Input '{parameter.Name}' expects {parameter.Type} but received {value.Kind}.", Stage: SkillValidationStage.Structural));
        }

        if (request.Skill.Preconditions.RequiresWritableWorkspace && string.IsNullOrWhiteSpace(request.WorkspacePath)) diagnostics.Add(new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL019", "A writable workspace is required.", Stage: SkillValidationStage.Policy));
        if (request.Skill.CapabilityPolicy.RequiresIsolation && string.IsNullOrWhiteSpace(request.WorkspacePath)) diagnostics.Add(new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL020", "This Skill requires an isolated workspace.", Stage: SkillValidationStage.Safety));
        if (request.Skill.Preconditions.Scope is { Kind: SkillScopeKind.Project or SkillScopeKind.Repository } && string.IsNullOrWhiteSpace(request.ProjectKey)) diagnostics.Add(new SkillDiagnostic(SkillDiagnosticSeverity.Error, "SKILL021", "A project or repository scope is required.", Stage: SkillValidationStage.Structural));
        return diagnostics;
    }

    private static SkillExecutionResult Failure(SkillDefinition skill, SkillExecutionStatus status, string summary, string code, long started, IReadOnlyList<SkillDiagnostic> diagnostics) => new(skill.Id, skill.Version, SkillExecutionId.New(), status, summary, null, null, SkillVerificationOutcome.Inconclusive, Stopwatch.GetElapsedTime(started), FailureCode: code, Diagnostics: diagnostics);
}

public sealed class NoOpSkillStepRunner : ISkillStepRunner
{
    public ValueTask<SkillStepResult> RunAsync(SkillStep skillStep, SkillExecutionContext context) =>
        ValueTask.FromResult(new SkillStepResult(true, $"Completed {skillStep.Label}.", Verification: skillStep is SkillVerificationStep ? SkillVerificationOutcome.Passed : SkillVerificationOutcome.NotRun));
}

public sealed class SkillPromotionPolicy(SkillEngineOptions? options = null) : ISkillPromotionPolicy
{
    private readonly SkillEngineOptions _options = options ?? new SkillEngineOptions();

    public SkillDefinition Apply(SkillDefinition skill, SkillValidationReport validation, SkillExecutionResult? result = null, bool userApproved = false)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.IsValid) return skill with { State = SkillLifecycleState.Rejected, UpdatedAt = DateTimeOffset.UtcNow };
        var next = skill;
        if (skill.State == SkillLifecycleState.Candidate) next = next with { State = SkillLifecycleState.Experimental };
        if (result is not null)
        {
            var statistics = skill.Statistics.Record(result);
            next = next with { Statistics = statistics, State = result.Verification == SkillVerificationOutcome.Passed ? skill.State : skill.State == SkillLifecycleState.Trusted ? SkillLifecycleState.NeedsRevalidation : skill.State, UpdatedAt = DateTimeOffset.UtcNow };
        }

        if (next.State == SkillLifecycleState.Experimental && next.Statistics.VerifiedSuccesses > 0) next = next with { State = SkillLifecycleState.Validated };
        if (next.State == SkillLifecycleState.Validated && next.Statistics.Executions >= _options.TrustedMinimumExecutions && next.Statistics.Reliability >= _options.TrustedMinimumReliability && next.Statistics.RecentFailures < _options.TrustedFailureWindow && (!_options.RequireUserApprovalForTrusted || userApproved)) next = next with { State = SkillLifecycleState.Trusted };
        if (next.State == SkillLifecycleState.Trusted && next.Statistics.RecentFailures >= _options.TrustedFailureWindow) next = next with { State = SkillLifecycleState.NeedsRevalidation };
        return next;
    }
}

public sealed class SkillEngine(
    ISkillRegistry registry,
    ISkillMatcher matcher,
    ISkillValidator validator,
    ISkillExecutor executor,
    ISkillPromotionPolicy promotion,
    SkillEngineOptions? options = null)
{
    private readonly SkillEngineOptions _options = options ?? new SkillEngineOptions();

    public ISkillRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));
    public ISkillMatcher Matcher { get; } = matcher ?? throw new ArgumentNullException(nameof(matcher));
    public ISkillValidator Validator { get; } = validator ?? throw new ArgumentNullException(nameof(validator));
    public ISkillExecutor Executor { get; } = executor ?? throw new ArgumentNullException(nameof(executor));
    public ISkillPromotionPolicy Promotion { get; } = promotion ?? throw new ArgumentNullException(nameof(promotion));

    public IReadOnlyList<SkillMatch> Match(SkillMatchRequest request, int limit = 8) => Matcher.Match(request, limit);

    public async ValueTask<SkillExecutionResult?> TryExecuteMatchedAsync(SkillMatchRequest request, IReadOnlyDictionary<string, SkillParameterValue>? inputs = null, MissionId? missionId = null, string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        var matches = Matcher.Match(request, 1);
        var match = matches.Count == 0 ? null : matches[0];
        if (match is null || match.Score < _options.MinimumAutomaticMatchScore) return null;
        var result = await Executor.ExecuteAsync(new SkillExecutionRequest(
            match.Skill,
            inputs,
            missionId,
            request.SpecialistRole,
            request.ProjectKey,
            workspacePath,
            AllowMutation: request.AllowMutation), cancellationToken).ConfigureAwait(false);
        var updated = Promotion.Apply(match.Skill, Validator.Validate(match.Skill), result);
        Registry.Register(updated, replace: true);
        await Registry.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public SkillDefinition Record(SkillDefinition skill, SkillExecutionResult result, bool userApproved = false)
    {
        var updated = Promotion.Apply(skill, Validator.Validate(skill), result, userApproved);
        Registry.Register(updated, replace: true);
        return updated;
    }
}
