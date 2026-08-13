using Abraxius.App;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Protocol;
using Abraxius.Runtime;
using Abraxius.Models;
using Abraxius.Security;
using Abraxius.Telemetry;
using Xunit;

namespace Abraxius.Runtime.Tests;

public sealed class RuntimeAndUiTests
{
    [Fact]
    public async Task ConfiguredModelCredentialIsRegisteredAsOpaqueBrokeredSecret()
    {
        var variable = $"ABRAXIUS_TEST_MODEL_SECRET_{Guid.NewGuid():N}";
        var value = $"model-secret-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var intelligence = new IntelligenceFabricOptions
            {
                Frontier = new GatewayConnectionOptions
                {
                    Enabled = true,
                    Endpoint = "https://models.example.test/v1/chat/completions",
                    DefaultModel = "test-model",
                    ApiKeyEnvironmentVariable = variable
                }
            };
            await using var runtime = AbraxiusRuntimeHost.CreateDefault(new RuntimeHostOptions(
                UseFileEvidence: false, UseFileLedger: false, UseFileProgression: false,
                UseFilePresence: false, UseFileSecurity: false, Intelligence: intelligence));

            var secrets = await runtime.Security.Secrets.ListAsync();
            var metadata = Assert.Single(secrets, secret => secret.Reference == new SecretReference("secret://model/frontier"));
            Assert.Equal(new SecretReference("secret://model/frontier"), metadata.Reference);
            Assert.DoesNotContain(value, System.Text.Json.JsonSerializer.Serialize(metadata), StringComparison.Ordinal);
            Assert.Contains(runtime.Security.Grants.ListActive(DateTimeOffset.UtcNow), grant =>
                grant.Scope == GrantScope.Session && grant.ResourcePrefix == metadata.Reference.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task FinalizedMissionFlowsIntoProgressionAsynchronously()
    {
        await using var runtime = AbraxiusRuntimeHost.CreateDefault(new RuntimeHostOptions(UseFileEvidence: false, UseFileLedger: false, UseFileProgression: false,
            Intelligence: new IntelligenceFabricOptions { UseDeterministicMockModel = true }));
        await runtime.StartAsync();
        var rewarded = new TaskCompletionSource<Abraxius.Progression.MissionRewardRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Progression.RewardCommitted += (_, reward) => rewarded.TrySetResult(reward);

        var mission = await runtime.RunMissionAsync(new Intent("@Orion find ExecutionGraph", CorrelationId.New()), explicitRole: SpecialistRole.Investigator);
        var reward = await rewarded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(mission.Mission.Id, reward.MissionId);
        Assert.Equal(1, runtime.Progression.Snapshot.Career.Missions);
        Assert.True(runtime.Progression.Snapshot.Specialists[SpecialistRole.Investigator].Experience > 0);
    }

    [Fact]
    public async Task RuntimeDemoProducesSuccessfulCorrelatedExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "abraxius-tests", Guid.NewGuid().ToString("N"));
        await using var runtime = AbraxiusRuntimeHost.CreateDefault(new RuntimeHostOptions(
            LedgerPath: Path.Combine(root, "events.jsonl"),
            EvidencePath: Path.Combine(root, "evidence"),
            UseFileEvidence: true,
            Intelligence: new IntelligenceFabricOptions { UseDeterministicMockModel = true }));

        var result = await runtime.RunDemoAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(6, result.Tasks.Count);
        Assert.All(result.Tasks.Values, task => Assert.Equal(result.ExecutionId, task.ExecutionId));
        Assert.True(File.Exists(Path.Combine(root, "events.jsonl")));
    }

    [Fact]
    public async Task ProductionIntelligenceDoesNotInventAResponseWithoutAConfiguredGateway()
    {
        await using var runtime = AbraxiusRuntimeHost.CreateDefault(new RuntimeHostOptions(
            UseFileEvidence: false,
            UseFileLedger: false,
            UseFileProgression: false,
            UseFilePresence: false,
            UseFileSecurity: false,
            Intelligence: new IntelligenceFabricOptions()));

        Assert.Equal(0, runtime.Intelligence.Snapshot.CandidateCount);

        var exception = await Assert.ThrowsAsync<IntelligenceRoutingException>(() =>
            runtime.Model.InferAsync(new ModelRequest("Answer this for the real provider.")).AsTask());

        Assert.Equal("no_eligible_model_route", exception.Error.Code);
        Assert.DoesNotContain("demo", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UiStateCoalescesHighFrequencyEvents()
    {
        await using var hub = new RuntimeEventHub();
        var dispatcher = new CountingDispatcher();
        var snapshots = 0;
        await using var aggregator = new RuntimeUiStateAggregator(hub, dispatcher, _ => Interlocked.Increment(ref snapshots));
        await aggregator.StartAsync();

        var execution = ExecutionId.New();
        for (var i = 0; i < 250; i++)
        {
            await hub.PublishAsync(new TaskProgressEvent(
                DateTimeOffset.UtcNow,
                execution,
                TaskId.New(),
                CorrelationId.New(),
                "test",
                i / 250d,
                null));
        }

        await Task.Delay(120);
        Assert.True(dispatcher.PostCount < 250);
        Assert.True(snapshots > 0);
    }

    [Fact]
    public async Task UiStateProducesTypedBlocksAndAgentSnapshotsFromRuntimeEvents()
    {
        await using var hub = new RuntimeEventHub();
        var dispatcher = new CountingDispatcher();
        var snapshotReady = new TaskCompletionSource<UiGraphSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var aggregator = new RuntimeUiStateAggregator(hub, dispatcher, snapshot => snapshotReady.TrySetResult(snapshot));
        await aggregator.StartAsync();

        var execution = ExecutionId.New();
        var task = TaskId.New();
        var correlation = CorrelationId.New();
        await hub.PublishAsync(new ExecutionStartedEvent(DateTimeOffset.UtcNow, execution, correlation, "test", 1));
        await hub.PublishAsync(new TaskCreatedEvent(DateTimeOffset.UtcNow, execution, task, correlation, "Scout", "Search", ExecutorKind.Tool, WorkPriority.Interactive, []));
        await hub.PublishAsync(new TaskStartedEvent(DateTimeOffset.UtcNow, execution, task, correlation, "Scout", ExecutorKind.Tool, 1));
        await hub.PublishAsync(new TaskProgressEvent(DateTimeOffset.UtcNow, execution, task, correlation, "Scout", 0.5, "searching"));
        await hub.PublishAsync(new TaskCompletedEvent(DateTimeOffset.UtcNow, execution, task, correlation, "Scout", ResultId.New(), [EvidenceId.New()], new TaskTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5)), "found"));

        var latest = await snapshotReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(latest.Tasks);
        Assert.Equal(WorkState.Succeeded, latest.Tasks[0].State);
        Assert.Single(latest.Agents);
        Assert.Contains(latest.Blocks, block => block.Kind == ActivityBlockKind.Result);
        Assert.Single(latest.Tasks[0].Evidence!);
    }

    [Fact]
    public async Task ChatRendersTheCompletedModelResponseInsteadOfLeavingAPlaceholder()
    {
        var chat = new ChatViewModel(new MockModelProvider(TimeSpan.Zero), new CountingDispatcher());
        chat.Input = "Explain the scheduler briefly.";

        await chat.SendAsync();

        var assistant = Assert.Single(chat.Messages, static message => message.IsAssistant);
        Assert.False(assistant.IsStreaming);
        Assert.False(assistant.IsError);
        Assert.Contains("Deterministic test response", assistant.Text, StringComparison.Ordinal);
        Assert.Contains("Explain the scheduler briefly.", assistant.Text, StringComparison.Ordinal);
        Assert.Equal("READY · RESPONSE COMPLETE", chat.Status);

        await chat.DisposeAsync();
    }

    [Fact]
    public async Task ChatUsesAthenaIdentityAndRoutingContextByDefault()
    {
        var provider = new CapturingModelProvider();
        var chat = new ChatViewModel(provider, new CountingDispatcher());
        chat.Input = "What should we do next?";

        await chat.SendAsync();

        Assert.NotNull(provider.LastRequest);
        Assert.Contains("You are Athena", provider.LastRequest!.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("not as the underlying model provider", provider.LastRequest.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("Athena", provider.LastRequest.Metadata!["conversation.specialist"]);
        Assert.Equal("Athena", provider.LastRequest.Metadata["coordinator"]);
        Assert.Equal("ATHENA", chat.Messages.Single(static message => message.IsAssistant).Speaker.Split(" · ")[1]);

        await chat.DisposeAsync();
    }

    [Fact]
    public async Task ChatUsesAgentReachOnlyForAnExplicitUrlAndPassesTheReadAsReferenceMaterial()
    {
        var provider = new CapturingModelProvider();
        var calls = 0;
        ValueTask<CapabilityResult> Research(string url, CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult(new CapabilityResult(
                true,
                "Read through Agent Reach.",
                [EvidenceId.New()],
                new Dictionary<string, string>
                {
                    ["source"] = url,
                    ["route"] = "agent-reach/jina-reader",
                    ["content"] = "The page says the scheduler uses bounded queues."
                }));
        }

        var chat = new ChatViewModel(provider, new CountingDispatcher(), webResearch: Research);
        chat.Input = "Read this page and summarize it: https://example.com/scheduler";

        await chat.SendAsync();

        Assert.Equal(1, calls);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal("agent-reach", provider.LastRequest!.Metadata!["web.research"]);
        Assert.Contains("BEGIN WEB CONTENT", provider.LastRequest.Prompt, StringComparison.Ordinal);
        Assert.Contains("bounded queues", provider.LastRequest.Prompt, StringComparison.Ordinal);

        await chat.DisposeAsync();
    }

    [Fact]
    public async Task ChatMentionUsesRegisteredSpecialistProfile()
    {
        var provider = new CapturingModelProvider();
        var chat = new ChatViewModel(provider, new CountingDispatcher());
        chat.Input = "@Orion inspect the evidence";
        chat.SelectSuggestionCommand.Execute(chat.Suggestions.Single(static suggestion => suggestion.Value == "Orion"));

        await chat.SendAsync();

        Assert.NotNull(provider.LastRequest);
        Assert.Contains("You are Orion", provider.LastRequest!.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal("Orion", provider.LastRequest.Metadata!["conversation.specialist"]);
        Assert.Contains("CONVERSATIONAL OWNER: Orion", provider.LastRequest.Prompt, StringComparison.Ordinal);
        Assert.Contains("Evidence-led repository", provider.LastRequest.SystemPrompt, StringComparison.Ordinal);

        await chat.DisposeAsync();
    }

    [Fact]
    public async Task ChatStreamingBufferCoalescesFastTokensWithoutChangingFinalText()
    {
        var dispatcher = new CountingDispatcher();
        var visible = new List<string>();
        await using var buffer = new ChatStreamingBuffer(dispatcher, text => visible.Add(text), TimeSpan.FromMilliseconds(40));

        for (var i = 0; i < 500; i++)
        {
            buffer.Append("token ");
        }

        var final = await buffer.CompleteAsync();

        Assert.Equal(500 * "token ".Length, final.Length);
        Assert.Equal(final, visible[^1]);
        Assert.True(dispatcher.PostCount <= 2);
        Assert.True(buffer.FlushCount <= 2);
    }

    [Fact]
    public void ChatMarkdownProducesSafeTypedBlocks()
    {
        var blocks = ChatMarkdownParser.Parse("# Heading\n\n- one\n- two\n\n```csharp\nreturn 1;\n```\n\n<script>alert('x')</script>");

        Assert.Contains(blocks, static block => block is ChatHeadingBlock);
        Assert.Contains(blocks, static block => block is ChatListBlock);
        Assert.Contains(blocks, static block => block is ChatCodeFenceBlock);
        Assert.DoesNotContain(blocks, static block => block is null);
    }

    [Fact]
    public void ChatMarkdownPreservesParagraphAndListText()
    {
        var blocks = ChatMarkdownParser.Parse("Hello from Abraxius.\n\n- Core abilities\n- Streaming answers");

        Assert.Contains(blocks, static block => block is ChatParagraphBlock { Text: "Hello from Abraxius." });
        var list = Assert.Single(blocks.OfType<ChatListBlock>());
        Assert.Equal(["Core abilities", "Streaming answers"], list.Items);
    }

    [Fact]
    public async Task ChatComposerSupportsSpecialistSuggestionsContextAndMissionMode()
    {
        var chat = new ChatViewModel(new MockModelProvider(TimeSpan.Zero), new CountingDispatcher(), ["Athena", "Orion"]);

        chat.Input = "@Or";
        Assert.Contains(chat.Suggestions, static suggestion => suggestion.Value == "Orion");
        chat.SelectSuggestionCommand.Execute(chat.Suggestions.Single(static suggestion => suggestion.Value == "Orion"));
        Assert.Equal("Orion", chat.ActiveSpecialist);

        chat.AddProjectContextCommand.Execute(null);
        Assert.True(chat.HasContext);
        chat.ToggleModeCommand.Execute(null);
        Assert.True(chat.IsMissionMode);
        Assert.True(chat.ShowRunAction);
        Assert.False(chat.ShowSendAction);

        await chat.DisposeAsync();
    }

    [Fact]
    public async Task NavigationSelectionHasOneStateDrivenActiveItem()
    {
        await using var runtime = AbraxiusRuntimeHost.CreateDefault(new RuntimeHostOptions(
            UseFileEvidence: false, UseFileLedger: false, UseFileProgression: false,
            UseFilePresence: false, UseFileSecurity: false));
        await using var viewModel = new MainViewModel(runtime, dispatcher: new CountingDispatcher(), ownsRuntime: false);

        viewModel.SelectRail(RailDestination.Chat);
        var chat = viewModel.NavigationGroups.SelectMany(static group => group.Items).Single(static item => item.Destination == RailDestination.Chat);
        var mission = viewModel.NavigationGroups.SelectMany(static group => group.Items).Single(static item => item.Destination == RailDestination.Mission);
        Assert.True(chat.IsSelected);
        Assert.False(mission.IsSelected);

        viewModel.SelectRail(RailDestination.Mission);
        Assert.False(chat.IsSelected);
        Assert.True(mission.IsSelected);
        Assert.Single(viewModel.NavigationGroups.SelectMany(static group => group.Items), static item => item.IsSelected);
    }

    [Fact]
    public void CommandSearchRanksTitleMatches()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor("mission.run", "Run mission", "Submit intent", "Mission", "Ctrl+Enter", _ => ValueTask.CompletedTask));
        registry.Register(new CommandDescriptor("panel.terminal", "Open terminal", "Open a process surface", "Workspace", "Ctrl+`", _ => ValueTask.CompletedTask));

        var result = registry.Search("terminal");

        Assert.Single(result);
        Assert.Equal("panel.terminal", result[0].Id);
    }

    [Fact]
    public async Task EventHubSequencesEventsAndLedgerReadsHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "abraxius-ledger-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "events.jsonl");
        await using var ledger = new Abraxius.Ledger.BufferedEventLedger(path, capacity: 8, batchSize: 4);
        ledger.Start();
        await ledger.AppendAsync(new RuntimeWarningEvent(DateTimeOffset.UtcNow, ExecutionId.New(), null, CorrelationId.New(), "test", "warning"));
        await ledger.AppendAsync(new RuntimeWarningEvent(DateTimeOffset.UtcNow, ExecutionId.New(), null, CorrelationId.New(), "test", "warning-2"));
        await ledger.FlushAsync();

        var entries = new List<Abraxius.Ledger.LedgerEntry>();
        await foreach (var entry in ledger.ReadAsync())
        {
            entries.Add(entry);
        }

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(RuntimeEventKind.RuntimeWarning, entry.Kind));
    }

    private sealed class CapturingModelProvider : IModelProvider
    {
        public ModelRequest? LastRequest { get; private set; }

        public ValueTask<ModelResult> InferAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new ModelResult(
                "Captured response.",
                null,
                "test-model",
                null,
                TimeSpan.Zero,
                "test"));
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            yield return new ModelStreamEvent.Started(DateTimeOffset.UtcNow, "test-model");
            await Task.Yield();
            yield return new ModelStreamEvent.Token(DateTimeOffset.UtcNow, "Captured response.");
            yield return new ModelStreamEvent.Completed(
                DateTimeOffset.UtcNow,
                new ModelResult("Captured response.", null, "test-model", null, TimeSpan.Zero, "test"));
        }
    }

    private sealed class CountingDispatcher : IUiDispatcher
    {
        public int PostCount;
        public void Post(Action action)
        {
            Interlocked.Increment(ref PostCount);
            action();
        }
    }
}
