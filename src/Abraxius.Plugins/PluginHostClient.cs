using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugin.Contracts.Protocol;
using Grpc.Core;
using Grpc.Net.Client;

namespace Abraxius.Plugins;

public sealed record PluginHostCommand(string FileName, string? ManagedEntryAssembly = null)
{
    public static PluginHostCommand ForManagedEntry(string managedEntryAssembly)
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return new(configured, managedEntryAssembly);
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var runtimeRootHost = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", executable));
        return new(File.Exists(runtimeRootHost) ? runtimeRootHost : executable, managedEntryAssembly);
    }

    public ProcessStartInfo Create(string bootstrapHandle)
    {
        var start = new ProcessStartInfo(FileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (ManagedEntryAssembly is not null) start.ArgumentList.Add(ManagedEntryAssembly);
        start.ArgumentList.Add("--bootstrap-handle"); start.ArgumentList.Add(bootstrapHandle);
        start.Environment.Clear();
        foreach (var pair in SanitizedPluginEnvironment.Create()) start.Environment[pair.Key] = pair.Value;
        return start;
    }
}

public sealed record PluginHostLaunchOptions(PluginHostCommand Command, string IpcRoot, TimeSpan StartupTimeout, PluginHostResourceBudget Budget)
{
    public static PluginHostLaunchOptions Create(PluginHostCommand command, string ipcRoot) => new(command, ipcRoot, TimeSpan.FromSeconds(15), new());
}

public interface IPluginHostSession : IAsyncDisposable
{
    PluginHostId HostId { get; }
    PluginHostSessionId SessionId { get; }
    PluginRegistration Registration { get; }
    PluginHealthState Health { get; }
    ValueTask<PluginInvocationResult> InvokeAsync(PluginInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<PluginHealthState> CheckHealthAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(string reason, CancellationToken cancellationToken = default);
}

public interface IPluginHostLauncher { ValueTask<IPluginHostSession> LaunchAsync(PluginInstallation installation, PluginHostLaunchOptions options, CancellationToken cancellationToken = default); }

public sealed class LocalGrpcPluginHostLauncher : IPluginHostLauncher
{
    public async ValueTask<IPluginHostSession> LaunchAsync(PluginInstallation installation, PluginHostLaunchOptions options, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.IpcRoot);
        var endpointKind = OperatingSystem.IsWindows() ? "named-pipe" : "unix-domain-socket";
        var endpoint = CreateEndpoint(endpointKind, options.IpcRoot);
        var sessionId = PluginHostSessionId.New(); var hostId = PluginHostId.New(); var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var bootstrap = new PluginHostBootstrap(endpointKind, endpoint, sessionId.ToString(), nonce, installation.PackageDirectory, installation.Package.Sha256, installation.Manifest, hostId.ToString());
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var process = new Process { StartInfo = options.Command.Create(pipe.GetClientHandleAsString()), EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("PluginHost process could not be started.");
        GrpcChannel? channel = null;
        try
        {
            pipe.DisposeLocalCopyOfClientHandle();
            // The child intentionally closes its inherited read handle immediately after the one-line
            // bootstrap. Do not dispose an AutoFlush StreamWriter afterward: a redundant second flush can
            // race that close and surface a false "broken pipe" startup failure.
            var writer = new StreamWriter(pipe, System.Text.Encoding.UTF8, 1024, leaveOpen: true);
            var bootstrapJson = JsonSerializer.Serialize(bootstrap, PluginContractJsonContext.Default.PluginHostBootstrap);
            await writer.WriteLineAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(bootstrapJson))).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            channel = CreateChannel(endpointKind, endpoint);
            var client = new PluginHostControl.PluginHostControlClient(channel);
            var deadline = DateTime.UtcNow + options.StartupTimeout;
            PluginHostWelcome? welcome = null; Exception? last = null;
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    welcome = await client.HandshakeAsync(new PluginHostHello { SessionId = sessionId.ToString(), Nonce = nonce, PluginId = installation.Package.PluginId.Value, PluginVersion = installation.Package.Version.ToString(), PackageHash = installation.Package.Sha256, MinimumProtocol = 1, MaximumProtocol = PluginProtocolVersion.Current.Value }, deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
                    break;
                }
                catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded) { last = exception; await Task.Delay(75, cancellationToken).ConfigureAwait(false); }
                catch (RpcException exception) { last = exception; break; }
            }
            if (welcome is null || !welcome.Accepted)
            {
                TryTerminate(process);
                try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); } catch (TimeoutException) { }
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var diagnostic = string.IsNullOrWhiteSpace(stderr) ? last?.Message ?? "process exited" : stderr.Trim();
                throw new InvalidOperationException(welcome?.Reason ?? $"PluginHost failed to start: {diagnostic}.");
            }
            var registration = JsonSerializer.Deserialize(welcome.RegistrationJson, PluginContractJsonContext.Default.PluginRegistration) ?? PluginRegistration.Empty;
            return new LocalGrpcPluginHostSession(process, channel, client, hostId, sessionId, registration, options.Budget, endpointKind, endpoint);
        }
        catch
        {
            TryTerminate(process); channel?.Dispose(); process.Dispose(); TryDeleteSocket(endpointKind, endpoint);
            throw;
        }
    }

    private static string CreateEndpoint(string endpointKind, string ipcRoot)
    {
        if (endpointKind == "named-pipe") return $"abraxius-plugin-{Guid.NewGuid():N}";
        var name = $"plugin-{Guid.NewGuid():N}.sock";
        var preferred = Path.Combine(ipcRoot, name);
        // Linux sockaddr_un is commonly limited to 108 bytes including its terminator. Keep a
        // conservative margin and use a randomized per-user temporary path when the store is deep.
        return System.Text.Encoding.UTF8.GetByteCount(preferred) <= 100
            ? preferred
            : Path.Combine(Path.GetTempPath(), $"axp-{Guid.NewGuid():N}.sock");
    }

    private static GrpcChannel CreateChannel(string endpointKind, string endpoint)
    {
        var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = false };
        handler.ConnectCallback = endpointKind == "named-pipe"
            ? async (_, token) => { var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous); await pipe.ConnectAsync(token).ConfigureAwait(false); return pipe; }
            : async (_, token) => { var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }
    private static void TryTerminate(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    internal static void TryDeleteSocket(string endpointKind, string endpoint) { if (endpointKind != "unix-domain-socket") return; try { File.Delete(endpoint); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

internal sealed class LocalGrpcPluginHostSession : IPluginHostSession
{
    private readonly Process process;
    private readonly GrpcChannel channel;
    private readonly PluginHostControl.PluginHostControlClient client;
    private readonly PluginHostResourceBudget budget;
    private readonly string endpointKind;
    private readonly string endpoint;
    private int _disposed;
    private readonly BoundedPluginLog _log = new(4 * 1024 * 1024, 10_000);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;

    public LocalGrpcPluginHostSession(Process process, GrpcChannel channel, PluginHostControl.PluginHostControlClient client, PluginHostId hostId, PluginHostSessionId sessionId, PluginRegistration registration, PluginHostResourceBudget budget, string endpointKind, string endpoint)
    {
        this.process = process; this.channel = channel; this.client = client; this.budget = budget; this.endpointKind = endpointKind; this.endpoint = endpoint;
        HostId = hostId; SessionId = sessionId; Registration = registration;
        _stdoutPump = PumpAsync(process.StandardOutput, "stdout");
        _stderrPump = PumpAsync(process.StandardError, "stderr");
    }

    public PluginHostId HostId { get; } public PluginHostSessionId SessionId { get; } public PluginRegistration Registration { get; }
    public PluginHealthState Health => process.HasExited ? PluginHealthState.Crashed : PluginHealthState.Healthy;
    public async ValueTask<PluginInvocationResult> InvokeAsync(PluginInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (process.HasExited) return new(invocation.InvocationId, PluginInvocationStatus.HostUnavailable, ErrorCode: "host-exited", ErrorMessage: "PluginHost is not running.");
        try
        {
            var response = await client.InvokeAsync(new PluginInvokeRequest { SessionId = SessionId.ToString(), InvocationId = invocation.InvocationId, ContributionId = invocation.ContributionId, PayloadJson = invocation.PayloadJson, TimeoutMilliseconds = Math.Clamp((long)invocation.Timeout.TotalMilliseconds, 1, 600_000), TraceParent = invocation.TraceParent ?? string.Empty }, deadline: DateTime.UtcNow + invocation.Timeout, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            return new(response.InvocationId, Enum.TryParse<PluginInvocationStatus>(response.Status, true, out var status) ? status : PluginInvocationStatus.Failed, response.PayloadJson, response.ErrorCode, response.ErrorMessage);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled) { return new(invocation.InvocationId, PluginInvocationStatus.Cancelled, ErrorCode: "cancelled", ErrorMessage: exception.Message); }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.DeadlineExceeded) { return new(invocation.InvocationId, PluginInvocationStatus.TimedOut, ErrorCode: "timeout", ErrorMessage: exception.Message); }
    }
    public async ValueTask<PluginHealthState> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (process.HasExited) return PluginHealthState.Crashed;
        try
        {
            var response = await client.HeartbeatAsync(new PluginHeartbeatRequest { SessionId = SessionId.ToString() }, deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);
            return response.Healthy && response.WorkingSetBytes <= budget.MemoryBytes ? PluginHealthState.Healthy : PluginHealthState.Unresponsive;
        }
        catch (RpcException) { return PluginHealthState.Unresponsive; }
    }
    public async ValueTask StopAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!process.HasExited) { try { await client.StopAsync(new PluginStopRequest { SessionId = SessionId.ToString(), Reason = reason }, deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false); } catch (RpcException) { } }
        if (!process.WaitForExit(3000)) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync("session-disposed").ConfigureAwait(false);
        await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false);
        channel.Dispose(); process.Dispose(); LocalGrpcPluginHostLauncher.TryDeleteSocket(endpointKind, endpoint);
    }

    private async Task PumpAsync(StreamReader reader, string stream)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                _log.Write(new(DateTimeOffset.UtcNow, stream, "plugin-host-output", line, ImmutableDictionary<string, string>.Empty));
        }
        catch (IOException) when (Volatile.Read(ref _disposed) != 0) { }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
    }
}

internal static class SanitizedPluginEnvironment
{
    private static readonly string[] Names = ["PATH", "DOTNET_ROOT", "LANG", "LC_ALL", "TMPDIR", "TEMP", "TMP", "SYSTEMROOT", "WINDIR"];
    public static IReadOnlyDictionary<string, string> Create()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names) if (Environment.GetEnvironmentVariable(name) is { } value) result[name] = value;
        return result;
    }
}
