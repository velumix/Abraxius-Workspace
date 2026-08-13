using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Abraxius.Core;
using Abraxius.Lattice;
using Abraxius.Protocol;
using Xunit;

namespace Abraxius.Lattice.Tests;

public sealed class AgentReachCapabilityTests
{
    [Fact]
    public async Task ReadsExplicitPublicUrlThroughBoundedAgentReachRouteAndStoresEvidence()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("# Agent Reach content")
        });
        var evidence = new TestEvidenceStore();
        using var capability = new AgentReachWebCapability(evidence, new HttpClient(handler));

        var result = await capability.ExecuteAsync(new CapabilityRequest(
            capability.Descriptor.Id,
            "read",
            "https://example.com/docs",
            null,
            CorrelationId.New(),
            ExecutionId.New(),
            TaskId.New()));

        Assert.True(result.Succeeded);
        Assert.Equal("https://r.jina.ai/https://example.com/docs", handler.LastRequest?.RequestUri?.AbsoluteUri);
        Assert.Equal("# Agent Reach content", result.Values!["content"]);
        Assert.Single(result.Evidence);
        Assert.Equal(result.Evidence[0], evidence.Stored!.Id);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/private")]
    [InlineData("https://localhost/private")]
    [InlineData("https://192.168.1.20/private")]
    public async Task RejectsLocalAndPrivateTargetsBeforeNetworkAccess(string target)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var capability = new AgentReachWebCapability(new TestEvidenceStore(), new HttpClient(handler));

        var result = await capability.ExecuteAsync(new CapabilityRequest(
            capability.Descriptor.Id,
            "read",
            target,
            null,
            CorrelationId.New(),
            ExecutionId.New(),
            TaskId.New()));

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_public_url", result.Error?.Code);
        Assert.Null(handler.LastRequest);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class TestEvidenceStore : IEvidenceStore
    {
        public EvidenceReference? Stored { get; private set; }

        public ValueTask<EvidenceReference> StoreAsync(EvidenceInput input, CancellationToken cancellationToken = default)
        {
            var bytes = input.Data.ToArray();
            Stored = new EvidenceReference(EvidenceId.New(), input.Kind, input.Name, input.ContentType, bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow, input.Metadata ?? new Dictionary<string, string>());
            return ValueTask.FromResult(Stored);
        }

        public ValueTask<EvidenceItem?> GetAsync(EvidenceId id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<EvidenceItem?>(null);
    }
}
