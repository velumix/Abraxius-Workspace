using System.Net;
using System.Net.Sockets;
using System.Text;
using Abraxius.Core;
using Abraxius.Protocol;

namespace Abraxius.Lattice;

/// <summary>
/// Read-only public web access for the installed Agent Reach workflow.
/// Agent Reach remains a routed capability, not an unrestricted shell surface.
/// </summary>
public sealed class AgentReachWebCapability : ILatticeCapability, IDisposable
{
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly Uri ReaderRoot = new("https://r.jina.ai/", UriKind.Absolute);
    private readonly IEvidenceStore _evidenceStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AgentReachWebCapability(IEvidenceStore evidenceStore, HttpClient? httpClient = null)
    {
        _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        _httpClient = httpClient ?? CreateHttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        "agent-reach.web",
        "Read one explicitly supplied public web page through the installed Agent Reach route.",
        "{ target: https://example.com/page, operation: read }",
        ExecutorKind.Io,
        true,
        ["read"],
        scope: "public-web",
        supportsCancellation: true,
        supportsStreaming: false,
        outputSchema: "{ source, route, content, evidenceId }");

    public async ValueTask<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Operation, "read", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("unsupported_operation", "Agent Reach web access only supports the read operation.");
        }

        if (!TryValidatePublicUrl(request.Target, out var source, out var validationError))
        {
            return Failure("invalid_public_url", validationError);
        }

        var readerUri = new Uri($"{ReaderRoot.AbsoluteUri}{source.AbsoluteUri}", UriKind.Absolute);
        try
        {
            using var response = await _httpClient.GetAsync(readerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failure("web_read_failed", $"Agent Reach could not read the page ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var content = new MemoryStream(capacity: Math.Min(MaximumResponseBytes, 64 * 1024));
            var buffer = new byte[16 * 1024];
            var total = 0;
            while (true)
            {
                var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaximumResponseBytes)
                {
                    return Failure("web_response_too_large", "Agent Reach web responses are bounded to 1 MiB.");
                }

                await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            var text = Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length)).Trim();
            if (text.Length == 0)
            {
                return Failure("web_response_empty", "Agent Reach returned an empty page.");
            }

            var reference = await _evidenceStore.StoreAsync(new EvidenceInput(
                "agent-reach.web",
                source.AbsoluteUri,
                Encoding.UTF8.GetBytes(text),
                "text/markdown",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = source.AbsoluteUri,
                    ["route"] = "agent-reach/jina-reader",
                    ["classification"] = "Public"
                }), cancellationToken).ConfigureAwait(false);

            return new CapabilityResult(
                true,
                $"Read {source.Host} through Agent Reach.",
                [reference.Id],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source"] = source.AbsoluteUri,
                    ["route"] = "agent-reach/jina-reader",
                    ["content"] = text,
                    ["evidenceId"] = reference.Id.ToString()
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return Failure("web_read_failed", $"Agent Reach could not reach the page: {exception.Message}");
        }
        catch (IOException exception)
        {
            return Failure("web_read_failed", $"Agent Reach could not read the page: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(8)
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static CapabilityResult Failure(string code, string message) =>
        new(false, null, [], Error: new RuntimeError(ErrorCategory.Tool, code, message));

    private static bool TryValidatePublicUrl(string target, out Uri source, out string error)
    {
        source = null!;
        error = "Only an explicit http or https URL can be read.";
        if (string.IsNullOrWhiteSpace(target) || target.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            !Uri.TryCreate(target.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        if (candidate.IsLoopback || candidate.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            candidate.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            candidate.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            error = "Local, link-local, and metadata-network URLs are not available through Agent Reach.";
            return false;
        }

        if (IPAddress.TryParse(candidate.Host, out var address) && IsPrivateAddress(address))
        {
            error = "Private and link-local network addresses are not available through Agent Reach.";
            return false;
        }

        source = candidate;
        error = string.Empty;
        return true;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }
}
