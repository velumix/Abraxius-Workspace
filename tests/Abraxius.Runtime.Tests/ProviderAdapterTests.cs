using System.Net;
using System.Net.Http;
using System.Text;
using Abraxius.Models;
using Xunit;

namespace Abraxius.Runtime.Tests;

public sealed class ProviderAdapterTests
{
    [Fact]
    public async Task OpenAiCompatibleAdapterParsesStructuredToolCalls()
    {
        var handler = new RecordingHandler("""
            {
              "model": "free-coder",
              "choices": [{
                "message": {
                  "content": "{\"ok\":true}",
                  "tool_calls": [{"function":{"name":"git_status","arguments":"{\"target\":\"repo\"}"}}]
                }
              }],
              "usage": {"prompt_tokens": 4, "completion_tokens": 3}
            }
            """);
        using var client = new HttpClient(handler);
        var provider = new OmniRouteModelProvider(client, new Uri("https://gateway.invalid/v1/chat/completions"));

        var result = await provider.InferAsync(new ModelRequest("inspect")
        {
            ExpectedJsonSchema = "{\"type\":\"object\"}",
            Tools = [new ModelToolDefinition("git_status", "Read git status", "{\"type\":\"object\"}")]
        });

        Assert.Equal("free-coder", result.Model);
        Assert.Equal(4, result.Usage!.InputTokens);
        var action = Assert.Single(result.Actions!);
        Assert.Equal("git_status", action.Operation);
        Assert.Equal("repo", action.Target);
        Assert.Contains("response_format", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("git_status", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiCompatibleAdapterStreamsSseDeltasAndCompletion()
    {
        var handler = new RecordingHandler(
            "data: {\"model\":\"stream-model\",\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
            "data: [DONE]\n\n",
            "text/event-stream");
        using var client = new HttpClient(handler);
        var provider = new LiteLlmModelProvider(client, new Uri("https://gateway.invalid/v1/chat/completions"));

        var events = new List<ModelStreamEvent>();
        await foreach (var item in provider.StreamAsync(new ModelRequest("stream")))
        {
            events.Add(item);
        }

        Assert.IsType<ModelStreamEvent.Started>(events[0]);
        Assert.Equal("hello", Assert.IsType<ModelStreamEvent.Token>(events[1]).Text);
        Assert.Equal(" world", Assert.IsType<ModelStreamEvent.Token>(events[2]).Text);
        Assert.Equal("hello world", Assert.IsType<ModelStreamEvent.Completed>(events[^1]).Result.Text);
    }

    private sealed class RecordingHandler(string body, string contentType = "application/json") : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
        }
    }
}
