using System.Net;
using System.Security.Cryptography.X509Certificates;
using Abraxius.Core;
using Abraxius.Fabric;
using Abraxius.Compute;
using Abraxius.Security;
using Abraxius.Plugins;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var configuration = NodeConfiguration.TryLoad();
if (configuration is null)
{
    Console.Error.WriteLine("Abraxius Node requires ABRAXIUS_FABRIC_ID, ABRAXIUS_NODE_ID, ABRAXIUS_NODE_CERTIFICATE, and ABRAXIUS_COORDINATOR_FINGERPRINT. Plaintext Fabric hosting is not permitted.");
    return 2;
}

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(configuration.CertificatePath, configuration.CertificatePassword);
var computeRoot = Environment.GetEnvironmentVariable("ABRAXIUS_NODE_COMPUTE_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "compute");
await using var compute = new ComputeRuntime(computeRoot);
await compute.RefreshAsync();
var pluginRoot = Environment.GetEnvironmentVariable("ABRAXIUS_NODE_PLUGIN_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "plugins");
await using var plugins = new PluginRuntime(pluginRoot, new LocalGrpcPluginHostLauncher(), PluginHostCommand.ForManagedEntry(Path.Combine(AppContext.BaseDirectory, "Abraxius.PluginHost.dll")));
await plugins.InitializeAsync();
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, configuration.Port, listen =>
{
    listen.Protocols = HttpProtocols.Http2;
    listen.UseHttps(new HttpsConnectionAdapterOptions
    {
        ServerCertificate = certificate,
        ClientCertificateMode = ClientCertificateMode.RequireCertificate,
        ClientCertificateValidation = (client, _, _) => client is not null && GrpcFabricTransport.Fingerprint(client).Value.Equals(configuration.CoordinatorFingerprint, StringComparison.OrdinalIgnoreCase)
    });
}));
builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 1024 * 1024; options.MaxSendMessageSize = 1024 * 1024; });
var descriptor = NodeDescriptorFactory.Create(configuration.NodeId, certificate).WithCompute(compute).WithPlugins(plugins);
var worker = new FabricWorker(descriptor, configuration.Epoch, new DelegateFabricLeaseExecutor(static (lease, _, token) =>
{
    token.ThrowIfCancellationRequested();
    if (lease.SideEffecting) throw new InvalidOperationException("The baseline node host has no configured side-effect reconciler.");
    return ValueTask.FromResult(WorkResult.Empty($"Remote {lease.Operation} completed on {lease.WorkerNodeId}."));
}));
var transferRoot = Environment.GetEnvironmentVariable("ABRAXIUS_NODE_ARTIFACT_CACHE") ?? Path.Combine(AppContext.BaseDirectory, "fabric-artifacts");
builder.Services.AddSingleton(new FabricGrpcService(configuration.FabricId, configuration.Epoch, worker, new GrpcFabricTransferStore(transferRoot)));
var app = builder.Build(); app.MapGrpcService<FabricGrpcService>(); app.MapGet("/", () => Results.Text("Abraxius Fabric node: gRPC/HTTP2/TLS required.")); await app.RunAsync(); return 0;

internal sealed record NodeConfiguration(FabricId FabricId, FabricNodeId NodeId, FabricEpoch Epoch, int Port, string CertificatePath, string? CertificatePassword, string CoordinatorFingerprint)
{
    public static NodeConfiguration? TryLoad()
    {
        if (!Guid.TryParse(Environment.GetEnvironmentVariable("ABRAXIUS_FABRIC_ID"), out var fabric) || !Guid.TryParse(Environment.GetEnvironmentVariable("ABRAXIUS_NODE_ID"), out var node)) return null;
        var path = Environment.GetEnvironmentVariable("ABRAXIUS_NODE_CERTIFICATE"); var fingerprint = Environment.GetEnvironmentVariable("ABRAXIUS_COORDINATOR_FINGERPRINT"); if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fingerprint)) return null;
        var port = int.TryParse(Environment.GetEnvironmentVariable("ABRAXIUS_NODE_PORT"), out var parsed) ? parsed : 7443; var epoch = ulong.TryParse(Environment.GetEnvironmentVariable("ABRAXIUS_FABRIC_EPOCH"), out var parsedEpoch) ? parsedEpoch : 1;
        return new(new(fabric), new(node), new(epoch), port, path, Environment.GetEnvironmentVariable("ABRAXIUS_NODE_CERTIFICATE_PASSWORD"), fingerprint);
    }
}

internal static class NodeDescriptorFactory
{
    public static FabricNodeDescriptor Create(FabricNodeId id, X509Certificate2 certificate)
    {
        var resources = new NodeResourceSnapshot(Environment.ProcessorCount, 0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, [], 0, NodePowerState.Unknown, false, new(null, null), DateTimeOffset.UtcNow);
        return new(id, Environment.MachineName, GrpcFabricTransport.Fingerprint(certificate), NodeTrustState.Trusted, FabricNodeRole.Worker | FabricNodeRole.ArtifactHost | FabricNodeRole.EvaluationWorker, Environment.OSVersion.Platform.ToString(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(), FabricProtocolVersion.Current, [new("Cpu", "1"), new("Io", "1"), new("Verification", "1")], [SandboxLevel.None], [], resources, new([], 0, 0), [], FabricNodeHealth.Healthy, FabricConnectivity.Connected, FabricSessionId.New(), LastSeen: DateTimeOffset.UtcNow);
    }
}
