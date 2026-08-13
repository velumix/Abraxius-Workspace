using System.Diagnostics;
using System.Text.Json;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugin.Contracts.Protocol;
using Grpc.Core;

namespace Abraxius.PluginHost;

internal sealed class PluginHostState(PluginHostBootstrap bootstrap, LoadedPlugin plugin, PluginRegistration registration)
{
    public PluginHostBootstrap Bootstrap { get; } = bootstrap;
    public LoadedPlugin Plugin { get; } = plugin;
    public PluginRegistration Registration { get; } = registration;
    public bool ValidateSession(string value) => CryptographicEquals(value, Bootstrap.SessionId);
    public bool ValidateNonce(string value) => CryptographicEquals(value, Bootstrap.Nonce);
    private static bool CryptographicEquals(string left, string right)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(left); var b = System.Text.Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}

internal sealed class PluginHostGrpcService(PluginHostState state, IHostApplicationLifetime lifetime) : PluginHostControl.PluginHostControlBase
{
    public override Task<PluginHostWelcome> Handshake(PluginHostHello request, ServerCallContext context)
    {
        var accepted = state.ValidateSession(request.SessionId) && state.ValidateNonce(request.Nonce) && request.PluginId == state.Bootstrap.Manifest.Id && request.PluginVersion == state.Bootstrap.Manifest.Version && request.PackageHash == state.Bootstrap.ExpectedPackageHash && request.MinimumProtocol <= PluginProtocolVersion.Current.Value && request.MaximumProtocol >= PluginProtocolVersion.Current.Value;
        return Task.FromResult(new PluginHostWelcome
        {
            Accepted = accepted,
            Reason = accepted ? "Authenticated local PluginHost session." : "PluginHost identity, package pin, or protocol did not match the launched session.",
            SelectedProtocol = accepted ? PluginProtocolVersion.Current.Value : 0,
            PluginApi = PluginApiVersion.Current.ToString(),
            HostId = state.Bootstrap.HostId,
            RegistrationJson = accepted ? JsonSerializer.Serialize(state.Registration, PluginContractJsonContext.Default.PluginRegistration) : string.Empty,
            Features = { "typed-invocation", "bounded-payloads", "declarative-ui" }
        });
    }

    public override async Task<PluginInvokeResponse> Invoke(PluginInvokeRequest request, ServerCallContext context)
    {
        if (!state.ValidateSession(request.SessionId)) throw new RpcException(new Status(StatusCode.Unauthenticated, "PluginHost session mismatch."));
        if (request.PayloadJson.Length > 4 * 1024 * 1024) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Invocation payload exceeds the configured bound."));
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMilliseconds, 1, 600_000));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken); linked.CancelAfter(timeout);
        PluginInvocationResult result;
        try { result = await state.Plugin.Instance.InvokeAsync(new(request.InvocationId, request.ContributionId, request.PayloadJson, timeout, string.IsNullOrWhiteSpace(request.TraceParent) ? null : request.TraceParent), linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { result = new(request.InvocationId, context.CancellationToken.IsCancellationRequested ? PluginInvocationStatus.Cancelled : PluginInvocationStatus.TimedOut, ErrorCode: "cancelled", ErrorMessage: "Plugin invocation was cancelled or timed out."); }
        catch (Exception exception) { result = new(request.InvocationId, PluginInvocationStatus.Failed, ErrorCode: "plugin-exception", ErrorMessage: exception.Message); }
        return new() { InvocationId = result.InvocationId, Status = result.Status.ToString(), PayloadJson = result.PayloadJson, ErrorCode = result.ErrorCode ?? string.Empty, ErrorMessage = result.ErrorMessage ?? string.Empty };
    }

    public override Task<PluginHeartbeatResponse> Heartbeat(PluginHeartbeatRequest request, ServerCallContext context)
    {
        if (!state.ValidateSession(request.SessionId)) throw new RpcException(new Status(StatusCode.Unauthenticated, "PluginHost session mismatch."));
        using var process = Process.GetCurrentProcess();
        return Task.FromResult(new PluginHeartbeatResponse { Healthy = true, WorkingSetBytes = process.WorkingSet64, ProcessCpuMilliseconds = (long)process.TotalProcessorTime.TotalMilliseconds });
    }

    public override Task<PluginStopResponse> Stop(PluginStopRequest request, ServerCallContext context)
    {
        if (!state.ValidateSession(request.SessionId)) throw new RpcException(new Status(StatusCode.Unauthenticated, "PluginHost session mismatch."));
        _ = Task.Run(async () => { await Task.Delay(50).ConfigureAwait(false); lifetime.StopApplication(); });
        return Task.FromResult(new PluginStopResponse { Accepted = true });
    }
}
