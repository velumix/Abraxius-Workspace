using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Abraxius.Plugin.Contracts;
using Abraxius.PluginHost;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var handleIndex = Array.IndexOf(args, "--bootstrap-handle");
if (handleIndex < 0 || handleIndex + 1 >= args.Length) return 64;
PluginHostBootstrap bootstrap;
using (var pipe = new AnonymousPipeClientStream(PipeDirection.In, args[handleIndex + 1]))
using (var reader = new StreamReader(pipe))
{
    var line = await reader.ReadLineAsync().ConfigureAwait(false);
    bootstrap = line is null
        ? throw new InvalidDataException("PluginHost bootstrap was not supplied.")
        : JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line)), PluginContractJsonContext.Default.PluginHostBootstrap)
            ?? throw new InvalidDataException("PluginHost bootstrap is invalid.");
}
var packageFile = Path.Combine(Directory.GetParent(bootstrap.PackageDirectory)?.FullName ?? string.Empty, "package.nupkg");
if (!File.Exists(packageFile)) throw new InvalidDataException("Pinned plugin package is missing.");
await using (var stream = new FileStream(packageFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
{
    var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(bootstrap.ExpectedPackageHash))) throw new InvalidDataException("Plugin package hash does not match the approved installation.");
}
await using var loaded = LoadedPlugin.Load(bootstrap);
var registration = await loaded.Instance.InitializeAsync(new HostPluginContext(bootstrap.Manifest)).ConfigureAwait(false);
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    if (bootstrap.EndpointKind == "named-pipe") options.ListenNamedPipe(bootstrap.EndpointAddress, listen => listen.Protocols = HttpProtocols.Http2);
    else options.ListenUnixSocket(bootstrap.EndpointAddress, listen => listen.Protocols = HttpProtocols.Http2);
});
builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 4 * 1024 * 1024; options.MaxSendMessageSize = 4 * 1024 * 1024; });
builder.Services.AddSingleton(new PluginHostState(bootstrap, loaded, registration));
var app = builder.Build();
app.MapGrpcService<PluginHostGrpcService>();
#pragma warning disable CA1416 // Guarded by the runtime OS check; analyzer does not flow it into the startup callback.
if (!OperatingSystem.IsWindows() && bootstrap.EndpointKind == "unix-domain-socket") app.Lifetime.ApplicationStarted.Register(() => File.SetUnixFileMode(bootstrap.EndpointAddress, UnixFileMode.UserRead | UnixFileMode.UserWrite));
#pragma warning restore CA1416
await app.RunAsync().ConfigureAwait(false);
return 0;
