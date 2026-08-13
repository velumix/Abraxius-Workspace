using Abraxius.Axl;
using Abraxius.Memory;
using Abraxius.Protocol;
using Abraxius.Voice;
using Xunit;

namespace Abraxius.Debrief.Tests;

public sealed class DebriefTests
{
    [Fact]
    public async Task PlannerCreatesVersionedGroundedPlanAndAxlProjection()
    {
        var memory = MemoryId.New();
        var evidence = EvidenceId.New();
        var resolver = new FixtureResolver(
        [
            new DebriefEvidence($"m:{memory}", "Cancellation finding", "Generation validation prevents stale completion from committing.", memory, [evidence], MemorySourceKind.VerifiedExecution, 1),
            new DebriefEvidence("m:inferred", "Open question", "The queue may be involved, but this is not verified.", Source: MemorySourceKind.ModelInference, Authority: 0.42)
        ]);
        var planner = new DeterministicDebriefPlanner(resolver);
        var plan = await planner.PlanAsync(new DebriefRequest(new DebriefSourceSet(Query: "scheduler cancellation"), DebriefMode.Postmortem));

        Assert.Equal(DebriefMode.Postmortem, plan.Mode);
        Assert.NotEmpty(plan.Chapters);
        Assert.Contains(plan.Claims, claim => claim.Status == DebriefClaimStatus.Supported);
        Assert.Contains(plan.Claims, claim => claim.Status == DebriefClaimStatus.Inferred);

        var axl = DebriefAxlProjection.Format(plan);
        var parsed = AxlPipeline.ParseAndValidate(axl);
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
    }

    [Fact]
    public async Task GroundingRejectsUnsupportedClaimsBeforePlayback()
    {
        var claim = new DebriefClaim("cl-1", "Verified statement.", ["e:1"], 1, 1, [Abraxius.Agents.SpecialistRole.Investigator], DebriefClaimStatus.Supported);
        var unsupported = new DebriefClaim("cl-2", "Unsupported statement.", [], 0, 0, [Abraxius.Agents.SpecialistRole.Investigator], DebriefClaimStatus.Unsupported);
        var plan = CreatePlan([claim, unsupported]);
        var chapter = plan.Chapters[0];
        var composer = new InjectingComposer(claim);
        var grounding = new DeterministicDebriefGroundingPolicy();
        var turns = await composer.ComposeAsync(plan, chapter, []);
        var verified = grounding.Verify(plan, chapter, turns, out var rejected);

        Assert.Single(rejected);
        Assert.Single(verified);
        Assert.DoesNotContain(verified, turn => turn.Text.Contains("hallucinated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlayStartsIncrementallyCachesAudioAndExportsCitations()
    {
        var memory = MemoryId.New();
        var evidence = EvidenceId.New();
        var resolver = new FixtureResolver([new DebriefEvidence($"m:{memory}", "Root cause", "Argus verified the stress test passed.", memory, [evidence], MemorySourceKind.VerifiedExecution, 1)]);
        var planner = new DeterministicDebriefPlanner(resolver);
        var engine = new DebriefEngine(
            planner,
            new GroundedDebriefDialogueComposer(),
            new DeterministicDebriefGroundingPolicy(),
            new InMemoryDebriefAudioCache());
        var playback = new InMemoryAudioPlaybackService();
        engine.ConfigureAudio(new InMemoryTextToSpeechProvider(), playback);
        var session = await engine.CreateAsync(new DebriefRequest(new DebriefSourceSet(Query: "stress test"), GenerateAudio: true));
        var result = await engine.PlayAsync(session);

        Assert.True(result.Succeeded);
        Assert.Equal(DebriefState.Completed, session.State);
        Assert.NotEmpty(playback.Frames);
        Assert.Contains(session.Turns, turn => turn.SafeSourceRefs.Any(source => source.StartsWith("e:", StringComparison.Ordinal)));
        await using var output = new MemoryStream();
        await engine.ExportTranscriptAsync(session, output);
        var transcript = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Sources:", transcript, StringComparison.Ordinal);
        await using var audio = new MemoryStream();
        await engine.ExportAudioAsync(session, audio);
        Assert.True(audio.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(audio.ToArray(), 0, 4));
    }

    [Fact]
    public async Task PauseCancelAndResumeDoNotEmitStalePlayback()
    {
        var resolver = new FixtureResolver([new DebriefEvidence("m:1", "Long source", "A verified source-grounded statement.", EvidenceIds: [EvidenceId.New()], Source: MemorySourceKind.SourceCode, Authority: 1)]);
        var engine = new DebriefEngine(new DeterministicDebriefPlanner(resolver), new GroundedDebriefDialogueComposer(), new DeterministicDebriefGroundingPolicy());
        engine.ConfigureAudio(new InMemoryTextToSpeechProvider(TimeSpan.FromMilliseconds(5)), new InMemoryAudioPlaybackService());
        var session = await engine.CreateAsync(new DebriefRequest(new DebriefSourceSet(Query: "source")));
        var play = engine.PlayAsync(session);
        await Task.Delay(25);
        await engine.PauseAsync(session);
        var paused = await play;
        Assert.Equal(DebriefState.Paused, session.State);
        Assert.False(paused.Succeeded);
        await engine.ResumeAsync(session);
        Assert.Equal(DebriefState.Completed, session.State);
        await engine.CancelAsync(session);
        Assert.Equal(DebriefState.Cancelled, session.State);
    }

    [Fact]
    public async Task LiveQuestionIsAnsweredFromCurrentDebriefWithoutDirectExecution()
    {
        var resolver = new FixtureResolver([new DebriefEvidence("m:1", "Evidence", "The current source supports the verified answer.", Source: MemorySourceKind.SourceCode, Authority: 1)]);
        var engine = new DebriefEngine(new DeterministicDebriefPlanner(resolver), new GroundedDebriefDialogueComposer(), new DeterministicDebriefGroundingPolicy());
        var session = await engine.CreateAsync(new DebriefRequest(new DebriefSourceSet(Query: "current source")));
        var answer = await engine.AskAsync(session, new DebriefLiveQuestion("Argus, how confident are you?", Abraxius.Agents.SpecialistRole.Verifier, ResumeAfterAnswer: false));

        Assert.Equal("Argus", answer.Turn.SpeakerName);
        Assert.Empty(answer.Evidence);
        Assert.Equal(DebriefState.Interrupted, session.State);
    }

    [Fact]
    public async Task JsonSessionStoreCanReopenSavedDebrief()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abraxius-debrief-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonDebriefSessionStore(path);
            var resolver = new FixtureResolver([new DebriefEvidence("m:1", "Source", "A persisted grounded fact.", Source: MemorySourceKind.SourceCode, Authority: 1)]);
            var engine = new DebriefEngine(new DeterministicDebriefPlanner(resolver), new GroundedDebriefDialogueComposer(), new DeterministicDebriefGroundingPolicy(), sessions: store);
            var session = await engine.CreateAsync(new DebriefRequest(new DebriefSourceSet(Query: "persisted"), GenerateAudio: false));
            await engine.PlayAsync(session);
            await engine.DisposeAsync();
            store.Dispose();

            var reopenedStore = new JsonDebriefSessionStore(path);
            var reopenedEngine = new DebriefEngine(new DeterministicDebriefPlanner(resolver), new GroundedDebriefDialogueComposer(), new DeterministicDebriefGroundingPolicy(), sessions: reopenedStore);
            var restored = await reopenedEngine.RestoreAsync(session.Id);
            Assert.NotNull(restored);
            Assert.Equal(session.Plan.SourceSnapshot.ContentHash, restored!.Plan.SourceSnapshot.ContentHash);
            Assert.Equal(session.Turns.Count, restored.Turns.Count);
            await reopenedEngine.DisposeAsync();
            reopenedStore.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public async Task DebriefBuilderActionDoesNotMutateDirectly()
    {
        var resolver = new FixtureResolver([]);
        var engine = new DebriefEngine(new DeterministicDebriefPlanner(resolver), new GroundedDebriefDialogueComposer(), new DeterministicDebriefGroundingPolicy());
        var session = await engine.CreateAsync(new DebriefRequest(new DebriefSourceSet(Query: "implementation"), GenerateAudio: false));
        var answer = await engine.AskAsync(session, new DebriefLiveQuestion("Daedalus, implement that fix", Abraxius.Agents.SpecialistRole.Builder, ResumeAfterAnswer: false));

        Assert.Contains("cannot mutate directly", answer.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(answer.Evidence);
    }

    private static EpisodePlan CreatePlan(IReadOnlyList<DebriefClaim> claims) =>
        new(EpisodePlanId.New(), DebriefMode.Briefing, "Test", "test", DebriefAudience.Developer, TimeSpan.FromMinutes(1),
            [Abraxius.Agents.SpecialistRole.Investigator],
            [new ChapterId(Guid.NewGuid()) is var id ? new DebriefChapter(id, 1, "Test", "test", [Abraxius.Agents.SpecialistRole.Investigator], claims.Select(static item => item.ClaimId).ToArray()) : throw new InvalidOperationException()],
            claims, new SourceSnapshot("test", new DebriefSourceSet(), DateTimeOffset.UtcNow, "test", new Dictionary<string, string>(), [], []), DateTimeOffset.UtcNow);

    private sealed class FixtureResolver(IReadOnlyList<DebriefEvidence> evidence) : IDebriefSourceResolver
    {
        public ValueTask<DebriefSourceResolution> ResolveAsync(DebriefSourceSet sources, string query, int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DebriefSourceResolution(
                new SourceSnapshot("fixture", sources, DateTimeOffset.UtcNow, "fixture", new Dictionary<string, string>(), evidence.Where(static item => item.MemoryId.HasValue).Select(static item => item.MemoryId!.Value).ToArray(), evidence.SelectMany(static item => item.SafeEvidenceIds).ToArray()),
                evidence.Take(limit).ToArray(), [], TimeSpan.Zero));
    }

    private sealed class InjectingComposer(DebriefClaim claim) : IDebriefDialogueComposer
    {
        public ValueTask<IReadOnlyList<DialogueTurn>> ComposeAsync(EpisodePlan plan, DebriefChapter chapter, IReadOnlyList<DialogueTurn> priorTurns, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DialogueTurn>>(
            [
                new DialogueTurn(DialogueTurnId.New(), chapter.Id, Abraxius.Agents.SpecialistRole.Investigator, "Orion", "Verified statement.", [claim.ClaimId], [EvidenceId.New()], DebriefSpeechStyle.Investigative, TimeSpan.FromSeconds(2)),
                new DialogueTurn(DialogueTurnId.New(), chapter.Id, Abraxius.Agents.SpecialistRole.Investigator, "Orion", "hallucinated unsupported conclusion", ["cl-2"], [], DebriefSpeechStyle.Investigative, TimeSpan.FromSeconds(2))
            ]);
    }
}
