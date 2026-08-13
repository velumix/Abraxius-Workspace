using Abraxius.Axl;
using Abraxius.Axl.Model;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Protocol;
using Abraxius.Models;
using Abraxius.Distribution;
using Abraxius.Distribution.Desktop;
using Abraxius.Platform;
using Abraxius.Runtime;
using Abraxius.Voice;
using Abraxius.Memory;
using Abraxius.Debrief;
using Abraxius.Skills;
using Abraxius.Progression;
using Abraxius.Presence;
using Abraxius.Security;
using Abraxius.Artifacts;
using Abraxius.Evaluation;
using Abraxius.Fabric;
using Abraxius.Compute;
using Abraxius.Design;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugins;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Immutable;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "demo";
if (command is "plugins" or "plugin")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var pluginHost = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UsePlugins = true });
    await pluginHost.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "list"; var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    static (PluginId Id, PluginVersion? Version) ParsePluginRef(string value) { var parts = value.Split('@', 2); return (new PluginId(parts[0]), parts.Length == 2 ? PluginVersion.Parse(parts[1]) : null); }
    switch (subcommand)
    {
        case "list":
            if (json) Console.WriteLine(JsonSerializer.Serialize(pluginHost.Plugins.List()));
            else foreach (var item in pluginHost.Plugins.List()) Console.WriteLine($"{item.Package.PluginId.Value,-42} {item.Package.Version,-12} {item.State,-12} {item.Health,-11} {item.Manifest.Publisher}");
            return 0;
        case "inspect":
        case "permissions":
        {
            var value = args.Skip(2).FirstOrDefault(); if (value is null) { Console.Error.WriteLine($"Usage: abraxius plugins {subcommand} <id[@version]>"); return 2; }
            var reference = ParsePluginRef(value); var item = pluginHost.Plugins.Registry.Find(reference.Id, reference.Version); if (item is null) { Console.Error.WriteLine("Plugin installation not found."); return 1; }
            if (json) Console.WriteLine(JsonSerializer.Serialize(item));
            else if (subcommand == "permissions") foreach (var permission in item.Manifest.Permissions) Console.WriteLine($"{permission.Id,-28} {permission.Risk,-14} granted={item.Grants.Any(grant => grant.PermissionId.Equals(permission.Id, StringComparison.OrdinalIgnoreCase))} scopes={string.Join(',', permission.ResourceScopes)}\n  {permission.Reason}");
            else Console.WriteLine($"{item.Manifest.Name}\nID {item.Package.PluginId}\nVersion {item.Package.Version}\nPublisher {item.Manifest.Publisher} · {item.PublisherTrust}\nPackage SHA-256 {item.Package.Sha256}\nSignature {item.Signature}\nState {item.State} · {item.Health}\nSandbox {item.Sandbox}\nPermissions {item.Manifest.Permissions.Length} · Contributions {item.Manifest.Contributions.Length}\nPath {item.PackageDirectory}\nError {item.LastError ?? "none"}");
            return 0;
        }
        case "validate":
        {
            var path = args.Skip(2).FirstOrDefault(); if (path is null) { Console.Error.WriteLine("Usage: abraxius plugins validate <package> [--developer] [--json]"); return 2; }
            var validation = await pluginHost.Plugins.ValidateAsync(path, args.Contains("--developer", StringComparer.OrdinalIgnoreCase)); if (json) Console.WriteLine(JsonSerializer.Serialize(validation)); else { Console.WriteLine($"Manifest              {(validation.Errors.Any(error => error.Contains("manifest", StringComparison.OrdinalIgnoreCase)) ? "FAIL" : "PASS")}\nPackage integrity     {validation.Signature.State}\nCompatibility         {(validation.Errors.Length == 0 ? "PASS" : "FAIL")}\nPermissions           {validation.Manifest.Permissions.Length} requested\nEntrypoints           {validation.Manifest.Entrypoints.Length}\nPackage hash          {validation.Identity.Sha256}"); foreach (var error in validation.Errors) Console.Error.WriteLine($"ERROR {error}"); foreach (var warning in validation.Warnings) Console.Error.WriteLine($"WARN  {warning}"); } return validation.Valid ? 0 : 1;
        }
        case "install":
        {
            var path = args.Skip(2).FirstOrDefault(); if (path is null) { Console.Error.WriteLine("Usage: abraxius plugins install <package> --approve=<permission> [--developer]"); return 2; }
            var inspection = await pluginHost.Plugins.ValidateAsync(path, args.Contains("--developer", StringComparer.OrdinalIgnoreCase)); if (!inspection.Valid) { foreach (var error in inspection.Errors) Console.Error.WriteLine(error); return 1; }
            var approved = args.Where(static value => value.StartsWith("--approve=", StringComparison.OrdinalIgnoreCase)).Select(static value => value["--approve=".Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var grants = inspection.Manifest.Permissions.Where(permission => approved.Contains(permission.Id)).Select(permission => new PluginPermissionGrant(permission.Id, permission.ResourceScopes, DateTimeOffset.UtcNow, Environment.UserName)).ToImmutableArray();
            var installed = await pluginHost.Plugins.InstallAsync(new(path, grants, PluginPublisherTrust.Unknown, args.Contains("--developer", StringComparer.OrdinalIgnoreCase))); Console.WriteLine(json ? JsonSerializer.Serialize(installed) : $"Installed {installed.Package.PluginId}@{installed.Package.Version}. It remains stopped until explicitly enabled."); return 0;
        }
        case "enable": case "disable": case "restart":
        {
            var value = args.Skip(2).FirstOrDefault(); if (value is null) return 2; var reference = ParsePluginRef(value);
            if (subcommand == "disable" || subcommand == "restart") await pluginHost.Plugins.DisableAsync(reference.Id, reference.Version);
            PluginInstallation lifecycle = pluginHost.Plugins.Registry.Find(reference.Id, reference.Version)!;
            if (subcommand == "enable" || subcommand == "restart") lifecycle = await pluginHost.Plugins.EnableAsync(reference.Id, reference.Version);
            Console.WriteLine(json ? JsonSerializer.Serialize(lifecycle) : $"{lifecycle.Package.PluginId}@{lifecycle.Package.Version} {lifecycle.State} / {lifecycle.Health}"); return 0;
        }
        case "uninstall":
        {
            var value = args.Skip(2).FirstOrDefault(); if (value is null) return 2; var reference = ParsePluginRef(value); var installed = pluginHost.Plugins.Registry.Find(reference.Id, reference.Version); if (installed is null) return 1;
            await pluginHost.Plugins.UninstallAsync(reference.Id, installed.Package.Version, args.Contains("--keep-data", StringComparer.OrdinalIgnoreCase)); Console.WriteLine("Plugin unregistered, grants revoked by lifecycle boundary, and no PluginHost remains. Immutable package cleanup is deferred."); return 0;
        }
        case "logs": Console.WriteLine("Plugin logs are bounded and attached to active PluginHost diagnostics; no active persisted log provider is configured in this build."); return 0;
        case "sources": Console.WriteLine("local-package\tExplicit local NuGet-compatible package path\nconfigured-feed\tProvider boundary available; no public marketplace configured"); return 0;
        case "update": Console.Error.WriteLine("Update requires an explicit candidate package so permission differences and side-by-side health can be reviewed. Use install with the new version, then activate it."); return 2;
        default: Console.Error.WriteLine("Usage: abraxius plugins [list|inspect|install|enable|disable|restart|uninstall|permissions|logs|validate|sources] [--json]"); return 2;
    }
}
if (command is "design" or "designs")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var designRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileArtifacts = true });
    await designRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
    var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    switch (subcommand)
    {
        case "status":
        {
            var health = await designRuntime.Design.GetHealthAsync();
            if (json) Console.WriteLine(JsonSerializer.Serialize(health));
            else Console.WriteLine($"DESIGN\n\nProvider                {health.Provider}\nState                   {health.State}\nCan generate            {health.CanGenerate}\nMessage                 {health.Message}");
            return health.State is DesignProviderConnectionState.Failed ? 1 : 0;
        }
        case "surfaces":
            if (json) Console.WriteLine(JsonSerializer.Serialize(designRuntime.Design.Surfaces.List()));
            else foreach (var surface in designRuntime.Design.Surfaces.List()) Console.WriteLine($"{surface.Id,-28} {surface.Category,-12} {surface.DisplayName}");
            return 0;
        case "capture":
        {
            var id = args.Skip(2).FirstOrDefault();
            if (id is null) { Console.Error.WriteLine("Usage: abraxius design capture <surface-id>"); return 2; }
            var surface = designRuntime.Design.Surfaces.Resolve(new DesignSurfaceId(id));
            var snapshot = await surface.CaptureAsync(new DesignCaptureRequest(DesignViewportProfile.Expanded, 1920, 1080, Mode: DesignCaptureMode.LiveContent));
            Console.WriteLine(json ? JsonSerializer.Serialize(snapshot) : $"{snapshot.Surface} {snapshot.Status} {snapshot.ContentIdentity}\n{snapshot.FailureReason ?? "Captured bytes are available to the interactive host."}");
            return snapshot.Status == DesignCaptureStatus.Captured ? 0 : 1;
        }
        case "generate":
        {
            var id = args.Skip(2).FirstOrDefault() ?? DesignSurfaceId.ChatWorkspace.Value;
            var objective = string.Join(' ', args.Skip(3).TakeWhile(value => !value.Equals("--json", StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(objective)) { Console.Error.WriteLine("Usage: abraxius design generate <surface-id> <objective>"); return 2; }
            try
            {
                var session = await designRuntime.Design.Orchestrator.GenerateAsync(new DesignSurfaceId(id), objective,
                    new DesignCaptureRequest(DesignViewportProfile.Expanded, 1920, 1080, Mode: DesignCaptureMode.SyntheticContent),
                    Abraxius.Security.DataClassification.Internal, 3);
                if (json) Console.WriteLine(JsonSerializer.Serialize(session));
                else { Console.WriteLine($"session={session.Id} state={session.State} candidates={session.Generation?.SafeCandidates.Length ?? 0}"); foreach (var candidate in session.Generation?.SafeCandidates ?? []) Console.WriteLine($"  {candidate.Id} {candidate.Title} artifact={candidate.ArtifactReference ?? "none"}"); }
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine($"Design generation failed: {exception.Message}"); return 1; }
        }
        default: Console.Error.WriteLine("Usage: abraxius design [status|surfaces|capture <surface>|generate <surface> <objective>] [--json]"); return 2;
    }
}

if (command is "compute" or "models")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var computeRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false });
    await computeRuntime.StartAsync();
    await computeRuntime.Compute.RefreshAsync();
    var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? (command == "compute" ? "status" : "list");
    static string Bytes(long? value) => value is null ? "unknown" : value.Value >= (1L << 30) ? $"{value.Value / (double)(1L << 30):0.0} GiB" : $"{value.Value / (double)(1L << 20):0.0} MiB";
    static ModelVariantDescriptor? Variant(ComputeRuntime runtime, string? id) => id is null ? null : runtime.Models.Find(new(id));
    if (command == "compute")
    {
        var snapshot = await computeRuntime.Compute.Telemetry.SnapshotAsync();
        switch (subcommand)
        {
            case "status":
                if (json) Console.WriteLine(JsonSerializer.Serialize(new { snapshot, devices = computeRuntime.Compute.Devices.Current, resident = computeRuntime.Compute.Residency.Instances, reservations = computeRuntime.Compute.Governor.Reservations }));
                else
                {
                    Console.WriteLine($"COMPUTE\n\nCPU                     {snapshot.CpuUtilization?.ToString("P0", CultureInfo.InvariantCulture) ?? "unknown"}\nRAM available           {Bytes(snapshot.RamAvailableBytes)} / {Bytes(snapshot.RamTotalBytes)}\nPressure                {snapshot.RamPressure}");
                    foreach (var device in computeRuntime.Compute.Devices.Current) { var state = snapshot.Find(device.Id); Console.WriteLine($"\n{device.Model}\n  {device.DeviceClass} · {device.MemoryArchitecture}\n  memory {Bytes(state?.MemoryUsedBytes)} / {Bytes(state?.MemoryBudgetBytes)}\n  utilization {(state?.Utilization?.ToString("P0", CultureInfo.InvariantCulture) ?? "unknown")} · temperature {(state?.TemperatureCelsius?.ToString("0.#", CultureInfo.InvariantCulture) ?? "unknown")} C\n  telemetry {device.Telemetry}"); }
                    Console.WriteLine($"\nResident models         {computeRuntime.Compute.Residency.Instances.Length}\nActive reservations     {computeRuntime.Compute.Governor.Reservations.Count(value => value.State is ReservationState.Granted or ReservationState.Active)}");
                }
                return 0;
            case "devices":
                if (json) Console.WriteLine(JsonSerializer.Serialize(computeRuntime.Compute.Devices.Current));
                else foreach (var device in computeRuntime.Compute.Devices.Current) Console.WriteLine($"{device.Id,-34} {device.DeviceClass,-11} {device.MemoryArchitecture,-10} {device.Vendor} {device.Model}");
                return 0;
            case "inspect":
            {
                var id = args.Skip(2).FirstOrDefault(); var device = computeRuntime.Compute.Devices.Current.FirstOrDefault(value => value.Id.Value.Equals(id, StringComparison.Ordinal));
                if (device is null) { Console.Error.WriteLine("Compute device not found."); return 1; }
                var state = snapshot.Find(device.Id); Console.WriteLine(json ? JsonSerializer.Serialize(new { device, state }) : $"{device.Model}\nID {device.Id}\nVendor {device.Vendor}\nClass {device.DeviceClass}\nArchitecture {device.Architecture}\nMemory {device.MemoryArchitecture} · dedicated {Bytes(device.DedicatedMemoryBytes)} · shared {Bytes(device.SharedMemoryBytes)}\nBudget {Bytes(state?.MemoryBudgetBytes)} · used {Bytes(state?.MemoryUsedBytes)}\nTelemetry {device.Telemetry}\nBackends {string.Join(',', device.BackendCapabilities)}"); return 0;
            }
            case "workloads":
            case "reservations":
                if (json) Console.WriteLine(JsonSerializer.Serialize(computeRuntime.Compute.Governor.Reservations));
                else foreach (var item in computeRuntime.Compute.Governor.Reservations) Console.WriteLine($"{item.Id} {item.State,-10} {item.Request.Priority,-20} {item.Request.Purpose} · RAM {Bytes(item.Request.RamBytes)} · device {Bytes(item.Request.DeviceMemoryBytes.Values.Sum())}");
                return 0;
            default: Console.Error.WriteLine("Usage: abraxius compute [status|devices|inspect <device>|workloads|reservations] [--json]"); return 2;
        }
    }

    switch (subcommand)
    {
        case "list":
            if (json) Console.WriteLine(JsonSerializer.Serialize(computeRuntime.Compute.Models.Variants));
            else foreach (var model in computeRuntime.Compute.Models.Variants) Console.WriteLine($"{model.Id,-48} {model.Format,-8} {model.Quantization,-10} {Bytes(model.FileSizeBytes),-10} {model.ValidationState}");
            return 0;
        case "inspect":
        case "variants":
        {
            var key = args.Skip(2).FirstOrDefault();
            var matches = computeRuntime.Compute.Models.Variants.Where(value => value.Id.Value.Equals(key, StringComparison.Ordinal) || value.LogicalModel.Value.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) { Console.Error.WriteLine("Model or exact variant not found."); return 1; }
            if (json) Console.WriteLine(JsonSerializer.Serialize(matches)); else foreach (var model in matches) Console.WriteLine($"{model.DisplayName}\n  variant {model.Id}\n  revision {model.Revision}\n  {model.Format} · {model.Quantization} · {Bytes(model.FileSizeBytes)} · context {model.ContextMaximum}\n  validation {model.ValidationState} · storage {model.StorageKind}\n  license {model.License.Identifier ?? "unknown"} · backends {string.Join(',', model.CompatibleBackends)}");
            return 0;
        }
        case "import":
        {
            var path = args.Skip(2).FirstOrDefault(); var logical = args.Skip(3).FirstOrDefault(); var revision = args.Skip(4).FirstOrDefault();
            if (path is null || logical is null || revision is null || !File.Exists(path)) { Console.Error.WriteLine("Usage: abraxius models import <path> <logical-model> <revision>"); return 2; }
            await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metadata = new ModelImportMetadata(new(logical), new(revision), "unknown", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), 4096, new(null, null, false, LicenseAcceptance.NotRequired), new("local-file", Path.GetFullPath(path), revision, Abraxius.Models.DataClassification.LocalOnly), [new BackendId("llama.cpp"), new BackendId("onnx")]);
            var imported = await computeRuntime.Compute.Store.ImportAsync(stream, Path.GetFileName(path), metadata); Console.WriteLine(json ? JsonSerializer.Serialize(imported) : $"Imported immutable unvalidated variant {imported.Id}. Run load validation before use."); return 0;
        }
        case "remove":
        {
            var variant = Variant(computeRuntime.Compute, args.Skip(2).FirstOrDefault()); if (variant is null) { Console.Error.WriteLine("Exact model variant not found."); return 1; }
            if (computeRuntime.Compute.Residency.Instances.Any(value => value.VariantId == variant.Id)) { Console.Error.WriteLine("Unload the model before removal. Active or resident variants are never deleted implicitly."); return 1; }
            var removed = await computeRuntime.Compute.Store.RemoveAsync(variant.Id); Console.WriteLine(removed ? "Model variant removed. Historical identity remains in trajectories/evals." : "Backend-managed or external model cannot be removed by the Abraxius model store."); return removed ? 0 : 1;
        }
        case "load":
        case "benchmark":
        {
            var variant = Variant(computeRuntime.Compute, args.Skip(2).FirstOrDefault()); if (variant is null) { Console.Error.WriteLine("Exact model variant not found."); return 1; }
            var request = new LocalInferenceRequest(variant.LogicalModel, [], Math.Min(4096, variant.ContextMaximum), subcommand == "benchmark" ? 16 : 1, InferencePriority.InteractiveUser, true, false, Abraxius.Models.DataClassification.LocalOnly, subcommand == "benchmark" ? "Reply with exactly: ready" : "", RequiredVariant: variant.Id);
            var decision = await computeRuntime.Compute.Admission.AdmitAsync(request); if (decision.Plan is null) { Console.Error.WriteLine(decision.Explanation); return 1; }
            if (subcommand == "load") { var instance = await computeRuntime.Compute.Residency.EnsureResidentAsync(decision.Plan); Console.WriteLine(json ? JsonSerializer.Serialize(instance) : $"Resident {instance.VariantId} via {instance.BackendId}; {Bytes(instance.DeviceMemoryBytes)} device memory."); return 0; }
            LocalInferenceEvent.Completed? benchmarkResult = null; await foreach (var item in computeRuntime.Compute.Inference.InferAsync(request)) if (item is LocalInferenceEvent.Completed inferenceComplete) benchmarkResult = inferenceComplete;
            if (benchmarkResult is null) return 1; Console.WriteLine(json ? JsonSerializer.Serialize(benchmarkResult.Telemetry) : $"TTFT {benchmarkResult.Telemetry.TimeToFirstToken.TotalMilliseconds:0.##} ms · prompt {benchmarkResult.Telemetry.PromptTokensPerSecond?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unknown"} tok/s · generation {benchmarkResult.Telemetry.GenerationTokensPerSecond?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unknown"} tok/s"); return 0;
        }
        case "unload":
        {
            var id = args.Skip(2).FirstOrDefault(); if (id is null) return 2; var unloaded = await computeRuntime.Compute.Residency.UnloadAsync(new(id)); Console.WriteLine(unloaded ? "Idle model unloaded." : "Model is not resident, is active, or its backend is unavailable."); return unloaded ? 0 : 1;
        }
        case "pull": Console.Error.WriteLine("No model source was specified. Register an authorized IModelSourceProvider; URLs and registry credentials are not accepted as implicit CLI authority."); return 2;
        case "quantize": Console.Error.WriteLine("No compatible IModelVariantBuilder is installed. Quantization never silently substitutes a variant."); return 2;
        default: Console.Error.WriteLine("Usage: abraxius models [list|inspect <id>|variants <logical>|import <path> <logical> <revision>|remove <variant>|load <variant>|unload <variant>|benchmark <variant>|pull|quantize] [--json]"); return 2;
    }
}

if (command == "fabric")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var fabricRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileFabric = true });
    await fabricRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
    static bool TryNode(string? value, out FabricNodeId id) { var parsed = Guid.TryParse(value, out var guid); id = new(guid); return parsed; }
    switch (subcommand)
    {
        case "status":
            Console.WriteLine($"FABRIC\n\nFabric                  {fabricRuntime.Fabric.Id}\nCoordinator epoch       {fabricRuntime.Fabric.Epoch}\nNodes                   {fabricRuntime.Fabric.Nodes.Count}\nOnline                  {fabricRuntime.Fabric.Nodes.Count(node => node.Connectivity == FabricConnectivity.Connected)}\nLocal node              {fabricRuntime.Fabric.LocalNode.Id}");
            return 0;
        case "nodes":
            foreach (var node in fabricRuntime.Fabric.Nodes.OrderBy(static node => node.DisplayName, StringComparer.OrdinalIgnoreCase)) Console.WriteLine($"{node.Id} {node.Health,-12} {node.TrustState,-10} {node.DisplayName} · {node.Platform}/{node.Architecture} · {node.Connectivity}");
            return 0;
        case "inspect":
        case "capabilities":
        {
            if (!TryNode(args.Skip(2).FirstOrDefault(), out var id)) { Console.Error.WriteLine($"Usage: abraxius fabric {subcommand} <node-id>"); return 2; }
            var node = fabricRuntime.Fabric.Nodes.FirstOrDefault(candidate => candidate.Id == id); if (node is null) { Console.Error.WriteLine("Node not found."); return 1; }
            if (subcommand == "capabilities") { foreach (var capability in node.Capabilities) Console.WriteLine($"{capability.Id,-30} v{capability.Version,-10} {(capability.ReadOnly ? "read-only" : "mutation")}"); return 0; }
            Console.WriteLine($"{node.DisplayName}\nNode {node.Id}\nFingerprint {node.Fingerprint}\nTrust {node.TrustState}\nHealth {node.Health}\nConnectivity {node.Connectivity}\nRoles {node.Roles}\nPlatform {node.Platform}/{node.Architecture}\nRuntime {node.RuntimeVersion}\nProtocol {node.Protocol.Minimum}-{node.Protocol.Maximum}\nCPU {node.Resources.CpuUtilization:P0} · RAM {node.Resources.FreeRamBytes}/{node.Resources.TotalRamBytes}\nCapabilities {node.Capabilities.Length} · Models {node.Models.Length} · GPUs {node.Resources.Gpus.Length}");
            return 0;
        }
        case "drain":
        case "resume":
        {
            if (!TryNode(args.Skip(2).FirstOrDefault(), out var id)) { Console.Error.WriteLine($"Usage: abraxius fabric {subcommand} <node-id>"); return 2; }
            var changed = subcommand == "drain" ? fabricRuntime.Fabric.Drain(id) : fabricRuntime.Fabric.Resume(id); Console.WriteLine(changed ? $"Node {subcommand} accepted." : "Node not found or not eligible."); return changed ? 0 : 1;
        }
        case "placement":
        {
            var capability = args.Skip(2).FirstOrDefault() ?? "Cpu"; var local = fabricRuntime.Fabric.LocalNode.Id;
            var placement = fabricRuntime.Fabric.Placement.Place(new(ExecutionId.New(), NodeId.New(), WorkKind.Cpu, capability, Abraxius.Security.DataClassification.Internal, local), fabricRuntime.Fabric.Nodes);
            Console.WriteLine(placement.Explanation); foreach (var candidate in placement.Candidates) Console.WriteLine($"  {candidate.NodeId} eligible={candidate.Eligible} score={candidate.Score:0.##} {string.Join("; ", candidate.Eligible ? candidate.Reasons : candidate.Rejections)}"); return placement.Placed ? 0 : 1;
        }
        case "transfers":
            Console.WriteLine("No active Artifact transfers. Transfer state is streamed and bounded; historical content remains in the Artifact store."); return 0;
        case "pair":
        case "unpair":
            Console.Error.WriteLine("Pairing and credential revocation require the interactive authenticated pairing surface; this headless CLI build does not accept reusable credentials on its command line."); return 2;
        default:
            Console.Error.WriteLine("Usage: abraxius fabric [status|nodes|inspect <node>|capabilities <node>|drain <node>|resume <node>|placement <capability>|transfers]"); return 2;
    }
}

if (command == "eval")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var evalRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileEvaluation = true, UseFileArtifacts = true });
    await evalRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "suites"; var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    static bool TryEvalRunId(string? text, out EvalRunId id) { var ok = Guid.TryParse(text, out var parsed); id = new(parsed); return ok; }
    switch (subcommand)
    {
        case "suites":
        {
            var suites = await evalRuntime.Evaluation.Store.ListSuitesAsync();
            if (json) Console.WriteLine(JsonSerializer.Serialize(suites));
            else foreach (var suite in suites.GroupBy(static item => item.Id).Select(static group => group.OrderByDescending(item => item.Version, StringComparer.Ordinal).First())) Console.WriteLine($"{suite.Id,-28} v{suite.Version,-9} {suite.Domain,-14} cases={suite.Cases.Length,-5} {suite.State}");
            return 0;
        }
        case "inspect":
        {
            var id = args.Skip(2).FirstOrDefault(); if (id is null) { Console.Error.WriteLine("Usage: abraxius eval inspect <suite>"); return 2; }
            var suite = await evalRuntime.Evaluation.Store.GetSuiteAsync(new(id)); if (suite is null) { Console.Error.WriteLine("Suite not found."); return 1; }
            if (json) Console.WriteLine(JsonSerializer.Serialize(suite)); else { Console.WriteLine($"{suite.Name} / {suite.Id} v{suite.Version}\n{suite.Description}\nDomain {suite.Domain} · {suite.Cases.Length} cases · {suite.State}"); foreach (var gate in suite.Gates) Console.WriteLine($"  gate {gate.Id}: {gate.MetricId} {gate.Mode} {gate.Threshold} [{gate.Severity}]"); }
            return 0;
        }
        case "run":
        {
            var id = args.Skip(2).FirstOrDefault() ?? "core.mission-smoke"; var suite = await evalRuntime.Evaluation.Store.GetSuiteAsync(new(id)); if (suite is null) { Console.Error.WriteLine("Suite not found."); return 2; }
            var candidateRef = args.SkipWhile(static item => !string.Equals(item, "--candidate", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "working-tree";
            var preset = args.Contains("--full", StringComparer.OrdinalIgnoreCase) ? EvalSamplingPreset.Full : args.Contains("--smoke", StringComparer.OrdinalIgnoreCase) ? EvalSamplingPreset.Smoke : EvalSamplingPreset.Standard;
            var environment = EvalEnvironmentCapture.Capture(typeof(AbraxiusRuntimeHost).Assembly.GetName().Version?.ToString() ?? "unknown", candidateRef, "headless", "axl/1", "security/1");
            var run = await evalRuntime.RunEvaluationAsync(new(suite, new(EvalCandidateId.New(), candidateRef, "reference", candidateRef), environment, Preset: preset));
            if (json) Console.WriteLine(JsonSerializer.Serialize(run)); else { Console.WriteLine($"run={run.Id} suite={run.SuiteId}/{run.SuiteVersion} candidate={run.Candidate.Reference} status={run.Status}"); foreach (var metric in run.Metrics) Console.WriteLine($"  {metric.MetricId,-30} {(metric.Value?.ToString("0.####", CultureInfo.InvariantCulture) ?? metric.Availability.ToString()),10} {metric.Unit} n={metric.SampleCount}"); Console.WriteLine($"artifact={run.ReportArtifactRevision?.ToString() ?? "none"} progression=ineligible"); }
            return run.Status is EvalRunStatus.Failed or EvalRunStatus.InfrastructureFailure ? 1 : 0;
        }
        case "compare":
        {
            if (!TryEvalRunId(args.Skip(2).FirstOrDefault(), out var baseline) || !TryEvalRunId(args.Skip(3).FirstOrDefault(), out var candidate)) { Console.Error.WriteLine("Usage: abraxius eval compare <baseline-run> <candidate-run> [--json]"); return 2; }
            try { var comparison = await evalRuntime.Evaluation.CompareAsync(baseline, candidate); if (json) Console.WriteLine(JsonSerializer.Serialize(comparison)); else { Console.WriteLine($"comparison={comparison.Id} environment-compatible={comparison.EnvironmentCompatible} release={(comparison.ReleaseBlocked ? "BLOCKED" : "PASS")}"); foreach (var delta in comparison.Deltas) Console.WriteLine($"  {delta.MetricId,-30} {delta.Classification,-12} Δ={delta.AbsoluteDelta?.ToString("+0.####;-0.####;0", CultureInfo.InvariantCulture) ?? "unknown"}"); foreach (var gate in comparison.Gates) Console.WriteLine($"  GATE {gate.GateId,-24} {gate.Status,-12} {gate.Explanation}"); } return comparison.ReleaseBlocked ? 1 : 0; } catch (InvalidOperationException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        }
        case "regressions":
        {
            var regressions = await evalRuntime.Evaluation.Store.ListRegressionsAsync(limit: 1000); if (json) Console.WriteLine(JsonSerializer.Serialize(regressions)); else foreach (var regression in regressions) Console.WriteLine($"{regression.Id} {regression.Severity,-10} {regression.SuiteId,-24} {regression.MetricId,-28} {regression.Delta:+0.####;-0.####;0} {regression.State}"); return regressions.Any(static item => item.Severity is EvalRegressionSeverity.Major or EvalRegressionSeverity.Critical && item.State == EvalRegressionState.Open) ? 1 : 0;
        }
        case "case":
        {
            var suiteId = args.Skip(2).FirstOrDefault(); var caseId = args.Skip(3).FirstOrDefault(); if (suiteId is null || caseId is null) { Console.Error.WriteLine("Usage: abraxius eval case <suite> <case>"); return 2; } var suite = await evalRuntime.Evaluation.Store.GetSuiteAsync(new(suiteId)); var item = suite?.Cases.FirstOrDefault(value => value.Id.Value.Equals(caseId, StringComparison.OrdinalIgnoreCase)); if (item is null) { Console.Error.WriteLine("Case not found."); return 1; } Console.WriteLine(json ? JsonSerializer.Serialize(item) : $"{item.Id} · {item.Name}\noperation={item.Input.Operation} determinism={item.Determinism} repeats={item.EffectiveRepeatCount} isolation={item.Environment.Isolation}\nexpected={item.ExpectedOutcome.ExactResult}"); return 0;
        }
        case "replay":
        {
            if (!TryEvalRunId(args.Skip(2).FirstOrDefault(), out var runId)) { Console.Error.WriteLine("Usage: abraxius eval replay <run> <case>"); return 2; } var caseId = args.Skip(3).FirstOrDefault(); var run = await evalRuntime.Evaluation.Store.GetRunAsync(runId); var replayResult = run?.CaseResults.FirstOrDefault(item => caseId is null || item.CaseId.Value.Equals(caseId, StringComparison.OrdinalIgnoreCase)); if (replayResult is null) { Console.Error.WriteLine("Historical case result not found."); return 1; } Console.WriteLine(json ? JsonSerializer.Serialize(replayResult) : $"Replay inspection only (external side effects disabled).\ncase={replayResult.CaseId} status={replayResult.Status} trajectory={replayResult.TrajectoryId ?? "none"}\nerrors={string.Join("; ", replayResult.Errors)}"); return 0;
        }
        case "gate":
        {
            if (!TryEvalRunId(args.Skip(2).FirstOrDefault(), out var runId)) { Console.Error.WriteLine("Usage: abraxius eval gate <run>"); return 2; } var run = await evalRuntime.Evaluation.Store.GetRunAsync(runId); if (run is null) { Console.Error.WriteLine("Run not found."); return 1; } var suite = await evalRuntime.Evaluation.Store.GetSuiteAsync(run.SuiteId, run.SuiteVersion); if (suite is null) return 1; var failed = false; foreach (var gate in suite.Gates) { var metric = run.Metrics.FirstOrDefault(item => item.MetricId == gate.MetricId); var status = metric?.Value is null || metric.SampleCount < gate.RequiredSampleSize ? "INCONCLUSIVE" : gate.Mode switch { EvalGateMode.AbsoluteMinimum when metric.Value >= gate.Threshold => "PASS", EvalGateMode.AbsoluteMaximum when metric.Value <= gate.Threshold => "PASS", EvalGateMode.ZeroTolerance when Math.Abs(metric.Value.Value) < double.Epsilon => "PASS", EvalGateMode.RelativeMaximumRegression or EvalGateMode.RelativeMinimumImprovement => "NEEDS BASELINE", _ => "FAIL" }; if (status == "FAIL" && gate.Severity is EvalGateSeverity.ReleaseBlocking or EvalGateSeverity.SecurityCritical) failed = true; Console.WriteLine($"{gate.Id,-28} {status,-14} observed={metric?.Value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "unknown"} {gate.Explanation}"); } return failed ? 1 : 0;
        }
        case "export":
        {
            if (!TryEvalRunId(args.Skip(2).FirstOrDefault(), out var runId)) { Console.Error.WriteLine("Usage: abraxius eval export <run> [path]"); return 2; } var run = await evalRuntime.Evaluation.Store.GetRunAsync(runId); if (run is null) return 1; var path = args.Skip(3).FirstOrDefault() ?? $"eval-{run.Id}.json"; await File.WriteAllTextAsync(path, JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"Exported immutable eval report to {path}."); return 0;
        }
        default:
            Console.Error.WriteLine("Usage: abraxius eval [suites|inspect <suite>|run <suite> [--candidate ref] [--smoke|--full]|compare <runA> <runB>|regressions|case <suite> <case>|replay <run> <case>|gate <run>|export <run>] [--json]"); return 2;
    }
}

if (command == "artifacts")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var artifactRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileLedger = true, UseFileArtifacts = true });
    await artifactRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "list";
    static bool TryArtifactId(string? value, out ArtifactId id) => ArtifactId.TryParse(value, out id);
    static bool TryRevisionId(string? value, out ArtifactRevisionId id) { var ok = Guid.TryParse(value, out var parsed); id = new ArtifactRevisionId(parsed); return ok; }
    switch (subcommand)
    {
        case "list":
        {
            var stateText = args.SkipWhile(static value => value != "--state").Skip(1).FirstOrDefault();
            var state = Enum.TryParse<ArtifactState>(stateText, true, out var parsedState) ? parsedState : (ArtifactState?)null;
            foreach (var item in await artifactRuntime.Artifacts.Store.QueryAsync(new ArtifactQuery(State: state, Limit: 500)))
                Console.WriteLine($"{item.Id} {item.Kind.Value,-18} {item.State,-20} {item.Title} · rev={item.CurrentRevision}");
            return 0;
        }
        case "inspect":
        case "revisions":
        {
            if (!TryArtifactId(args.Skip(2).FirstOrDefault(), out var id)) { Console.Error.WriteLine($"Usage: abraxius artifacts {subcommand} <artifact-id>"); return 2; }
            var item = await artifactRuntime.Artifacts.Store.GetAsync(id); if (item is null) { Console.Error.WriteLine("Artifact not found."); return 1; }
            if (subcommand == "inspect")
            {
                Console.WriteLine($"{item.Descriptor.Title}\n{item.Descriptor.Kind.Value} · {item.Descriptor.State}\nProducer: {item.Descriptor.Producer.DisplayName} ({item.Descriptor.Producer.PrincipalId})\nMission: {item.Descriptor.Provenance.MissionId}\nCurrent revision: {item.Descriptor.CurrentRevision}\nClassification: {item.Descriptor.Classification.Level}");
                Console.WriteLine($"Revisions: {item.Revisions.Length} · Verification: {item.SafeVerifications.Length} · Reviews: {item.SafeReviews.Length} · Approvals: {item.SafeApprovals.Length} · Integrations: {item.SafeIntegrations.Length} · Publications: {item.SafePublications.Length}");
            }
            else foreach (var revision in item.Revisions.OrderBy(static revision => revision.RevisionNumber)) Console.WriteLine($"v{revision.RevisionNumber,-4} {revision.Id} hash={revision.RevisionHash} parent={revision.ParentRevisionId?.ToString() ?? "none"}");
            return 0;
        }
        case "diff":
        {
            if (!TryArtifactId(args.Skip(2).FirstOrDefault(), out var id) || !TryRevisionId(args.Skip(3).FirstOrDefault(), out var revisionId)) { Console.Error.WriteLine("Usage: abraxius artifacts diff <artifact-id> <revision-id>"); return 2; }
            var item = await artifactRuntime.Artifacts.Store.GetAsync(id); var revision = item?.Revisions.SingleOrDefault(value => value.Id == revisionId); var parent = revision?.ParentRevisionId is { } parentId ? item?.Revisions.SingleOrDefault(value => value.Id == parentId) : null;
            if (item is null || revision is null || parent is null) { Console.Error.WriteLine("Revision or parent not found."); return 1; }
            await using var oldContent = await artifactRuntime.Artifacts.Content.OpenReadAsync(parent.Content.BlobId); await using var newContent = await artifactRuntime.Artifacts.Content.OpenReadAsync(revision.Content.BlobId);
            var diff = await artifactRuntime.Artifacts.Diffs.Resolve(parent, revision).CompareAsync(parent, oldContent, revision, newContent, new ArtifactDiffOptions());
            Console.WriteLine(diff.Summary); foreach (var hunk in diff.Hunks) { Console.WriteLine($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@"); foreach (var line in hunk.Lines) Console.WriteLine($"{line.Prefix}{line.Text}"); }
            if (diff.Truncated) Console.WriteLine("[diff truncated by configured review bounds]"); return 0;
        }
        case "reviews":
            foreach (var review in await artifactRuntime.Artifacts.Reviews.GetQueueAsync()) Console.WriteLine($"{review.Id} artifact={review.ArtifactId} revision={review.ArtifactRevisionId} state={review.State} created={review.CreatedAt:u}");
            return 0;
        case "approve":
        case "reject":
        {
            if (!TryArtifactId(args.Skip(2).FirstOrDefault(), out var id) || !TryRevisionId(args.Skip(3).FirstOrDefault(), out var revisionId)) { Console.Error.WriteLine($"Usage: abraxius artifacts {subcommand} <artifact-id> <revision-id> [reason]"); return 2; }
            var item = await artifactRuntime.Artifacts.Store.GetAsync(id); var review = item?.SafeReviews.LastOrDefault(value => value.ArtifactRevisionId == revisionId && value.State is ArtifactReviewState.Pending or ArtifactReviewState.Viewed or ArtifactReviewState.ChangesRequested);
            if (review is null) { Console.Error.WriteLine("No pending review exists for that exact revision."); return 1; }
            var reason = string.Join(' ', args.Skip(4)); var approval = await artifactRuntime.Artifacts.Reviews.DecideAsync(review.Id, new PrincipalId("user:local"), subcommand == "approve" ? ArtifactApprovalState.Approved : ArtifactApprovalState.Rejected, reason);
            Console.WriteLine($"{approval.State}: artifact {id} revision {approval.ArtifactRevisionId}. Approval does not grant integration or publication authority."); return 0;
        }
        case "verify":
        {
            if (!TryArtifactId(args.Skip(2).FirstOrDefault(), out var id) || !TryRevisionId(args.Skip(3).FirstOrDefault(), out var revisionId)) { Console.Error.WriteLine("Usage: abraxius artifacts verify <artifact-id> <revision-id>"); return 2; }
            var item = await artifactRuntime.Artifacts.Store.GetAsync(id); var revision = item?.Revisions.SingleOrDefault(value => value.Id == revisionId); if (item is null || revision is null) { Console.Error.WriteLine("Artifact revision not found."); return 1; }
            var intact = await artifactRuntime.Artifacts.Content.VerifyAsync(revision.Content); var verification = new ArtifactVerification(ArtifactVerificationId.New(), revisionId, new ArtifactProducer(new PrincipalId("system:artifact-integrity"), ArtifactProducerKind.System, "Artifact Store"), "Content integrity only", [new("Stored content hash", revision.Content.ContentHash, intact ? revision.Content.ContentHash : "mismatch", [], intact ? ArtifactVerificationResult.Passed : ArtifactVerificationResult.Failed)], [], intact ? ArtifactVerificationResult.Inconclusive : ArtifactVerificationResult.Failed, DateTimeOffset.UtcNow, Environment.OSVersion.ToString());
            await artifactRuntime.Artifacts.Service.AttachVerificationAsync(id, verification); Console.WriteLine(intact ? "Content integrity passed. Semantic outcome remains Inconclusive until Argus verification." : "Content integrity failed."); return intact ? 0 : 1;
        }
        case "verify-store":
        {
            var integrity = await artifactRuntime.Artifacts.Service.VerifyStoreAsync(); Console.WriteLine($"artifacts={integrity.ArtifactCount} revisions={integrity.RevisionCount} missing={integrity.MissingBlobs} corrupt={integrity.CorruptBlobs}"); return integrity.MissingBlobs + integrity.CorruptBlobs == 0 ? 0 : 1;
        }
        default:
            Console.Error.WriteLine("Usage: abraxius artifacts [list|inspect <id>|revisions <id>|diff <id> <revision>|verify <id> <revision>|reviews|approve <id> <revision>|reject <id> <revision>|verify-store]"); return 2;
    }
}

if (command is "security" or "secrets")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var securityRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileLedger = true, UseFileSecurity = true });
    await securityRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? (command == "security" ? "status" : "list");
    if (command == "secrets")
    {
        var secrets = await securityRuntime.Security.Secrets.ListAsync();
        foreach (var secret in secrets) Console.WriteLine($"{secret.Reference,-34} {secret.DisplayName,-22} provider={secret.Provider} expires={secret.ExpiresAt?.ToString("u") ?? "never"} scope={string.Join(',', secret.AllowedDestinations)}");
        if (subcommand is not ("list" or "inspect")) Console.Error.WriteLine("Secret values are never accepted as command-line arguments. Configure them through a secure platform adapter.");
        return subcommand is "list" or "inspect" ? 0 : 2;
    }

    switch (subcommand)
    {
        case "status":
        {
            var status = await securityRuntime.Security.GetStatusAsync();
            Console.WriteLine($"SECURITY\n\nPolicy                  {status.Preset}\nLockdown                {status.Lockdown}\n\nPending approvals       {status.PendingApprovals}\nActive grants           {status.ActiveGrants}\nSecrets                  {status.StoredSecrets}\nRecent denials          {status.RecentDenials}");
            foreach (var sandbox in status.SandboxAvailability) Console.WriteLine($"Sandbox {sandbox.Key,-20} {(sandbox.Value ? "Available" : "Unavailable")}");
            return 0;
        }
        case "grants":
            foreach (var grant in securityRuntime.Security.Grants.ListActive(DateTimeOffset.UtcNow)) Console.WriteLine($"{grant.GrantId} {grant.Subject.PrincipalId} {string.Join(',', grant.Capabilities)} {grant.Scope} expires={grant.ExpiresAt:u} uses={grant.Uses}/{grant.MaximumUses?.ToString(CultureInfo.InvariantCulture) ?? "∞"}");
            return 0;
        case "revoke":
            if (!Guid.TryParse(args.Skip(2).FirstOrDefault(), out var grantId)) { Console.Error.WriteLine("Usage: abraxius security revoke <grant-id>"); return 2; }
            Console.WriteLine(securityRuntime.Security.Grants.Revoke(new AuthorizationGrantId(grantId), "CLI revocation") ? "Grant revoked." : "Grant not found.");
            return 0;
        case "audit":
            await foreach (var item in securityRuntime.Security.Audit.QueryAsync(200)) Console.WriteLine($"{item.Timestamp:u} {item.Type,-24} {item.Principal,-24} {item.Action,-24} {item.Resource} {item.ReasonCode}");
            return 0;
        case "lockdown":
            securityRuntime.Security.Kernel.Lockdown = !args.Contains("off", StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"Lockdown: {securityRuntime.Security.Kernel.Lockdown}");
            return 0;
        case "policies":
            foreach (var rule in securityRuntime.Security.Policies.Rules) Console.WriteLine($"{rule.Layer,-12} {rule.Effect,-16} {rule.ActionPattern,-24} {rule.Id} · {rule.Explanation}");
            return 0;
        case "explain":
        {
            var operation = args.Skip(2).FirstOrDefault() ?? SecurityActions.FileRead;
            var target = args.Skip(3).FirstOrDefault() ?? Environment.CurrentDirectory;
            var canonicalizer = new ResourceCanonicalizer();
            var kind = operation.StartsWith("Network.", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Network : operation.StartsWith("Secret.", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Secret : ResourceKind.File;
            var request = await AuthorizationRequestFactory.CreateAsync(canonicalizer, SecuritySubject.System("cli-policy-test"), "policy-test", operation, kind, target,
                new AuthorizationContext(WorkspaceRoot: Environment.CurrentDirectory, AvailableSandbox: SandboxLevel.IsolatedWorkspace));
            var explanation = securityRuntime.Security.Kernel.Explain(request);
            Console.WriteLine($"Final: {explanation.Decision.Outcome} · {explanation.Decision.ReasonCode}\n{explanation.Decision.HumanExplanation}");
            foreach (var line in explanation.Trace) Console.WriteLine($"  {line}");
            return explanation.Decision.Outcome == AuthorizationOutcome.Deny ? 1 : 0;
        }
        default:
            Console.Error.WriteLine("Usage: abraxius security [status|grants|revoke <id>|policies|explain <action> <target>|audit|lockdown on|off] | secrets list");
            return 2;
    }
}

if (command is "presence" or "notifications" or "needs-you" or "background")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var presenceRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileLedger = true, UseFilePresence = true });
    await presenceRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? (command == "presence" ? "status" : "list");
    if (command == "presence" && subcommand == "status")
    {
        var snapshot = presenceRuntime.Presence.Background.Snapshot;
        Console.WriteLine("Runtime\nRunning");
        Console.WriteLine($"\nWindow\n{snapshot.WindowState}");
        Console.WriteLine("\nTray\nHost-dependent");
        Console.WriteLine($"\nMissions\n{snapshot.ActiveMissionCount} running");
        Console.WriteLine($"\nNeeds You\n{snapshot.PendingNeedsYouCount} pending");
        Console.WriteLine($"\nBackground\n{snapshot.BackgroundMode}");
        return 0;
    }
    if (command == "notifications" && subcommand == "list")
    {
        foreach (var item in presenceRuntime.Presence.Notifications.History) Console.WriteLine($"{item.Timestamp:u} {item.Category,-12} {item.Severity,-18} {item.Title}");
        var diagnostics = presenceRuntime.Presence.Notifications.Diagnostics;
        Console.WriteLine($"generated={diagnostics.Generated} native={diagnostics.NativeDelivered} in-app={diagnostics.InAppDelivered} suppressed={diagnostics.Suppressed} coalesced={diagnostics.Coalesced}");
        return 0;
    }
    if (command == "needs-you")
    {
        var items = await presenceRuntime.Presence.NeedsYou.ListAsync(includeResolved: args.Contains("--all", StringComparer.OrdinalIgnoreCase));
        if (subcommand == "inspect")
        {
            var idText = args.Skip(2).FirstOrDefault();
            var item = items.FirstOrDefault(value => value.Id.ToString().Equals(idText, StringComparison.OrdinalIgnoreCase));
            if (item is null) { Console.Error.WriteLine("Needs You item not found."); return 1; }
            Console.WriteLine($"{item.Id}\n{item.Source} · {item.Reason} · {item.State}\n{item.ContextSummary}\nMission: {item.MissionId}\nDeadline: {item.Deadline?.ToString("u") ?? "none"}");
            return 0;
        }
        foreach (var item in items) Console.WriteLine($"{item.Id} {item.Priority,-18} {item.Source,-10} {item.Reason,-26} {item.ContextSummary}");
        return 0;
    }
    if (command == "background" && subcommand is "pause" or "resume")
    {
        presenceRuntime.Presence.Background.SetMode(subcommand == "pause" ? BackgroundExecutionMode.PauseNonCritical : BackgroundExecutionMode.ContinueNormally);
        Console.WriteLine($"Background: {presenceRuntime.Presence.Background.Snapshot.BackgroundMode}");
        return 0;
    }
    Console.Error.WriteLine("Usage: abraxius presence status | notifications list | needs-you [list|inspect <id>] | background [pause|resume]");
    return 2;
}

if (command == "progression")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var progressionRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileLedger = true });
    await progressionRuntime.StartAsync();
    var progression = progressionRuntime.Progression;
    var snapshot = progression.Snapshot;
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
    switch (subcommand)
    {
        case "status":
            Console.WriteLine($"Prestige {snapshot.Prestige.Rank.Value}");
            Console.WriteLine($"Operator Level {snapshot.Operator.CurrentLevel}");
            Console.WriteLine($"{snapshot.Operator.ExperienceIntoLevel:N0} / {snapshot.Operator.ExperienceRequired:N0} XP ({snapshot.Operator.LifetimeExperience:N0} lifetime)");
            Console.WriteLine();
            foreach (var item in snapshot.Specialists.Where(static item => item.Key != SpecialistRole.DomainExpert).OrderBy(static item => item.Key)) Console.WriteLine($"{DisplaySpecialist(item.Key),-10} Lv {item.Value.Level,-3} {item.Value.Title}");
            Console.WriteLine();
            Console.WriteLine($"{snapshot.Career.TrustedSkillUses:N0} trusted Skill uses · {snapshot.Skills.Values.Count(static item => item.Mastered):N0} mastered");
            return 0;
        case "specialists":
            foreach (var item in snapshot.Specialists.Where(static item => item.Key != SpecialistRole.DomainExpert).OrderBy(static item => item.Key))
            {
                Console.WriteLine($"{DisplaySpecialist(item.Key)} / {item.Key} · level {item.Value.Level} · {item.Value.Title} · {item.Value.Experience:N0} XP");
                foreach (var category in item.Value.SafeCategories.OrderByDescending(static item => item.Value)) Console.WriteLine($"  {category.Key,-22} {category.Value:N0}");
            }
            return 0;
        case "achievements":
            foreach (var definition in progression.Achievements.Definitions)
            {
                var progress = snapshot.Achievements.GetValueOrDefault(definition.Id.Value) ?? new AchievementProgress(definition.Id, Target: definition.Target);
                Console.WriteLine($"{(progress.Unlocked ? "UNLOCKED" : "LOCKED"),-9} {definition.Name,-24} {progress.Current:N0}/{progress.Target:N0} · {definition.Description}");
            }
            return 0;
        case "prestige":
            if (args.Contains("--activate", StringComparer.OrdinalIgnoreCase))
            {
                var prestigeResult = await progression.ActivatePrestigeAsync();
                Console.WriteLine(prestigeResult.Activated ? $"Prestige {prestigeResult.State.Rank.Value} activated. Lifetime career and mastery were preserved." : $"Prestige unavailable: {string.Join("; ", prestigeResult.UnmetRequirements)}");
                return prestigeResult.Activated ? 0 : 1;
            }
            Console.WriteLine($"Prestige {snapshot.Prestige.Rank.Value} · available={snapshot.Prestige.Available}");
            foreach (var unmet in snapshot.Prestige.SafeUnmetRequirements) Console.WriteLine($"  ○ {unmet}");
            Console.WriteLine("Use --activate for the explicit atomic transition. No authority is unlocked.");
            return 0;
        case "career":
            Console.WriteLine($"missions={snapshot.Career.Missions:N0} verified={snapshot.Career.VerifiedMissions:N0} failed={snapshot.Career.FailedMissions:N0} cancelled={snapshot.Career.CancelledMissions:N0}");
            Console.WriteLine($"verification-rate={snapshot.Career.VerificationRate:P1} extreme={snapshot.Career.ExtremeMissions:N0} defects-caught={snapshot.Career.DefectsCaught:N0}");
            Console.WriteLine($"parallel-branches={snapshot.Career.MeaningfulParallelBranches:N0} peak-meaningful-concurrency={snapshot.Career.PeakMeaningfulConcurrency:N0}");
            Console.WriteLine($"free/included-rate={snapshot.Career.FreeOrIncludedRate:P1} frontier-missions={snapshot.Career.FrontierMissions:N0}");
            return 0;
        case "rebuild":
            await progression.RebuildSnapshotAsync();
            Console.WriteLine($"Rebuilt progression snapshot from {progression.Snapshot.Career.Missions:N0} immutable reward records without awarding new XP.");
            return 0;
        case "reward":
        {
            var mission = args.Skip(2).FirstOrDefault();
            MissionRewardRecord? found = null;
            await foreach (var reward in progression.ReadRewardsAsync()) if (mission is not null && reward.MissionId.ToString().Equals(mission, StringComparison.OrdinalIgnoreCase)) { found = reward; break; }
            if (found is null) { Console.Error.WriteLine("Reward not found. Usage: abraxius progression reward <mission-id>"); return 1; }
            Console.WriteLine($"Base XP                 {found.BaseExperience:N0}");
            foreach (var factor in found.Factors) Console.WriteLine($"{factor.Name,-23} ×{factor.Value:0.00}  {factor.Explanation}");
            Console.WriteLine($"Final XP                {found.OperatorXp:N0}");
            Console.WriteLine($"Difficulty              {found.Difficulty} ({found.DifficultyScore:0.00})");
            Console.WriteLine($"Rules                   {found.RulesVersion}");
            return 0;
        }
        default:
            Console.Error.WriteLine("Usage: abraxius progression [status|specialists|achievements|prestige [--activate]|career|rebuild|reward <mission-id>]");
            return 2;
    }
}

static string DisplaySpecialist(SpecialistRole role) => role switch
{
    SpecialistRole.Coordinator => "Athena", SpecialistRole.Investigator => "Orion",
    SpecialistRole.Builder => "Daedalus", SpecialistRole.Verifier => "Argus", _ => role.ToString()
};

if (command == "agents" || command == "missions")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var agentRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileLedger = command == "missions" });
    await agentRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? (command == "agents" ? "list" : "list");
    if (command == "agents")
    {
        switch (subcommand)
        {
            case "list":
            case "status":
                foreach (var definition in agentRuntime.Agents.Registry.Definitions)
                {
                    var instances = agentRuntime.Agents.Registry.Instances.Where(instance => instance.DefinitionId == definition.Id).ToArray();
                    var state = instances.FirstOrDefault()?.State.ToString() ?? "Ready";
                    Console.WriteLine($"{definition.DisplayName,-10} {definition.Role,-14} {state,-12} instances={instances.Length} mutation={definition.CapabilityPolicy.Mutation}");
                }
                return 0;
            case "inspect":
                var name = args.Skip(2).FirstOrDefault() ?? "athena";
                if (!agentRuntime.Agents.Registry.TryResolve(name, out var definitionToInspect))
                {
                    Console.Error.WriteLine($"Unknown specialist '{name}'.");
                    return 2;
                }
                Console.WriteLine($"{definitionToInspect.DisplayName} / {definitionToInspect.Role}");
                Console.WriteLine($"mission: {definitionToInspect.Mission.Summary}");
                Console.WriteLine($"capabilities: {string.Join(", ", definitionToInspect.CapabilityPolicy.AllowedCapabilities)}");
                Console.WriteLine($"mutation: {definitionToInspect.CapabilityPolicy.Mutation}");
                Console.WriteLine($"workspace: {definitionToInspect.WorkspacePolicy.Mode}");
                Console.WriteLine($"model route: {definitionToInspect.ModelPolicy.RouteProfile}");
                return 0;
            case "run":
                var objective = string.Join(' ', args.Skip(2));
                if (string.IsNullOrWhiteSpace(objective))
                {
                    Console.Error.WriteLine("Usage: abraxius agents run <objective>");
                    return 2;
                }
                var mission = await agentRuntime.RunMissionAsync(new Intent(objective, CorrelationId.New()));
                Console.WriteLine($"mission={mission.Mission.Id} state={mission.Mission.State} duration={mission.Duration.TotalMilliseconds:F0}ms");
                Console.WriteLine(mission.Summary);
                foreach (var assignment in mission.AssignmentResults)
                {
                    Console.WriteLine($"  {assignment.Key} {(assignment.Value.Succeeded ? "PASS" : "FAIL")} {assignment.Value.Summary}");
                }
                return mission.Succeeded ? 0 : 1;
            default:
                Console.Error.WriteLine("Usage: abraxius agents [list|status|inspect <name>|run <objective>]");
                return 2;
        }
    }

    switch (subcommand)
    {
        case "list":
            foreach (var mission in agentRuntime.Agents.Missions)
            {
                Console.WriteLine($"{mission.Id} {mission.State} {mission.Intent.Objective}");
            }
            return 0;
        case "run":
            var objective = ExtractDebriefObjective(args.Skip(2).ToArray());
            if (string.IsNullOrWhiteSpace(objective))
            {
                Console.Error.WriteLine("Usage: abraxius missions run <objective>");
                return 2;
            }
            var missionResult = await agentRuntime.RunMissionAsync(new Intent(objective, CorrelationId.New()));
            Console.WriteLine($"{missionResult.Mission.Id} {missionResult.Mission.State} {missionResult.Summary}");
            return missionResult.Succeeded ? 0 : 1;
        case "inspect":
        case "trace":
            var missionId = args.Skip(2).FirstOrDefault();
            var record = agentRuntime.Agents.MissionRecords.FirstOrDefault(item => missionId is not null && item.Mission.Id.ToString().Equals(missionId, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                Console.Error.WriteLine("Mission not found in the local mission store.");
                return 1;
            }
            Console.WriteLine($"mission={record.Mission.Id} state={record.Mission.State}");
            Console.WriteLine($"objective={record.Mission.Intent.Objective}");
            Console.WriteLine($"summary={record.Summary}");
            Console.WriteLine($"assignments={record.Mission.SafeAssignments.Length} results={record.ResultCount}");
            if (subcommand == "trace") foreach (var assignment in record.Mission.SafeAssignments) Console.WriteLine($"  assignment={assignment}");
            return 0;
        default:
            Console.Error.WriteLine("Usage: abraxius missions [list|run <objective>]");
            return 2;
    }
}

if (command == "skills")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var skillRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileLedger = true });
    await skillRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "list";
    switch (subcommand)
    {
        case "list":
        case "status":
            foreach (var skill in skillRuntime.Skills.Registry.List(includeDisabled: true))
            {
                Console.WriteLine($"{skill.Id,-38} {skill.Version,-10} {skill.State,-18} reliability={skill.Statistics.Reliability:P0} executions={skill.Statistics.Executions} enabled={skill.Enabled}");
            }
            return 0;
        case "inspect":
        {
            if (!TryResolveSkill(skillRuntime.Skills.Registry, args.Skip(2).FirstOrDefault(), out var skill))
            {
                Console.Error.WriteLine("Usage: abraxius skills inspect <id>[@<version>]");
                return 2;
            }

            Console.WriteLine($"{skill.Id}/{skill.Version} [{skill.State}] origin={skill.Origin} enabled={skill.Enabled}");
            Console.WriteLine(skill.Description);
            var scopeText = skill.Preconditions.Scope is { } scope ? $"{scope.Kind}:{scope.Key}" : "global";
            Console.WriteLine($"scope={scopeText} category={skill.Category} safety={skill.CapabilityPolicy.Safety}");
            Console.WriteLine($"triggers={string.Join(", ", skill.Triggers.SafeConcepts.Concat(skill.Triggers.SafeTaskClasses).Concat(skill.Triggers.SafeErrorCodes))}");
            Console.WriteLine($"capabilities={string.Join(", ", skill.Preconditions.SafeCapabilities.Concat(skill.CapabilityPolicy.SafeCapabilities))}");
            Console.WriteLine("steps:");
            foreach (var step in skill.Procedure.SafeSteps)
            {
                Console.WriteLine($"  {step.Id} {step.Kind} deps=[{string.Join(',', step.SafeDependencies)}] {step.Label}");
            }
            Console.WriteLine($"verify={string.Join("; ", skill.Verification.SafeCriteria)}");
            Console.WriteLine("AXL:");
            Console.WriteLine(SkillAxlProjection.Format(skill, AxlFormatMode.Pretty));
            return 0;
        }
        case "match":
        {
            var objective = string.Join(' ', args.Skip(2));
            if (string.IsNullOrWhiteSpace(objective))
            {
                Console.Error.WriteLine("Usage: abraxius skills match <objective>");
                return 2;
            }

            var matches = skillRuntime.Skills.Match(new SkillMatchRequest(objective, ProjectKey: CurrentProjectKey()), 8);
            if (matches.Count == 0)
            {
                Console.WriteLine("No eligible Skill matched.");
                return 1;
            }
            foreach (var match in matches)
            {
                Console.WriteLine($"{match.Skill.Id}/{match.Skill.Version} score={match.Score:0.000} state={match.Skill.State} reliability={match.Skill.Statistics.Reliability:P0}");
                Console.WriteLine($"  {match.Explanation}");
            }
            return 0;
        }
        case "validate":
        {
            if (!TryResolveSkill(skillRuntime.Skills.Registry, args.Skip(2).FirstOrDefault(), out var skill))
            {
                Console.Error.WriteLine("Usage: abraxius skills validate <id>[@<version>]");
                return 2;
            }

            var report = skillRuntime.Skills.Validator.Validate(skill, new SkillValidationOptions(AllowMutation: true, AvailableCapabilities: skill.Preconditions.SafeCapabilities.Concat(skill.CapabilityPolicy.SafeCapabilities).ToHashSet()));
            Console.WriteLine($"validation={report.ValidationId} valid={report.IsValid}");
            foreach (var diagnostic in report.Diagnostics) Console.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            return report.IsValid ? 0 : 1;
        }
        case "run":
        {
            if (!TryResolveSkill(skillRuntime.Skills.Registry, args.Skip(2).FirstOrDefault(), out var skill))
            {
                Console.Error.WriteLine("Usage: abraxius skills run <id>[@<version>] [--dry-run]");
                return 2;
            }

            var skillResult = await skillRuntime.Skills.Executor.ExecuteAsync(new SkillExecutionRequest(
                skill,
                ProjectKey: CurrentProjectKey(),
                WorkspacePath: Environment.CurrentDirectory,
                DryRun: args.Contains("--dry-run", StringComparer.Ordinal)));
            Console.WriteLine($"skill={skillResult.SkillId}/{skillResult.Version} execution={skillResult.ExecutionId} status={skillResult.Status} verification={skillResult.Verification} duration={skillResult.Duration.TotalMilliseconds:F0}ms");
            Console.WriteLine(skillResult.Summary);
            foreach (var diagnostic in skillResult.SafeDiagnostics) Console.WriteLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            return skillResult.Succeeded || skillResult.Status == SkillExecutionStatus.DryRun ? 0 : 1;
        }
        case "enable":
        case "disable":
        {
            if (!TryResolveSkill(skillRuntime.Skills.Registry, args.Skip(2).FirstOrDefault(), out var skill))
            {
                Console.Error.WriteLine($"Usage: abraxius skills {subcommand} <id>[@<version>]");
                return 2;
            }
            var enabled = subcommand == "enable";
            var changed = skillRuntime.Skills.Registry.TryUpdate(skill.Id, skill.Version, current => current with
            {
                Enabled = enabled,
                State = enabled && current.State == SkillLifecycleState.Disabled ? SkillLifecycleState.Experimental : enabled ? current.State : SkillLifecycleState.Disabled,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await skillRuntime.Skills.Registry.SaveAsync();
            Console.WriteLine(changed ? $"{skill.Id}/{skill.Version} {(enabled ? "enabled" : "disabled")}." : "Skill was not updated.");
            return changed ? 0 : 1;
        }
        case "promote":
        {
            if (!TryResolveSkill(skillRuntime.Skills.Registry, args.Skip(2).FirstOrDefault(), out var skill))
            {
                Console.Error.WriteLine("Usage: abraxius skills promote <id>[@<version>]");
                return 2;
            }
            var validation = skillRuntime.Skills.Validator.Validate(skill, new SkillValidationOptions(AllowMutation: true, AvailableCapabilities: skill.Preconditions.SafeCapabilities.Concat(skill.CapabilityPolicy.SafeCapabilities).ToHashSet()));
            var promoted = skillRuntime.Skills.Promotion.Apply(skill, validation, userApproved: true);
            skillRuntime.Skills.Registry.Register(promoted, replace: true);
            await skillRuntime.Skills.Registry.SaveAsync();
            Console.WriteLine($"{promoted.Id}/{promoted.Version} state={promoted.State} (promotion policy applied; verified-use thresholds still apply).");
            return validation.IsValid ? 0 : 1;
        }
        case "export":
        {
            if (!TryResolveSkill(skillRuntime.Skills.Registry, args.Skip(2).FirstOrDefault(), out var skill))
            {
                Console.Error.WriteLine("Usage: abraxius skills export <id>[@<version>] [path] [--axl]");
                return 2;
            }
            var axl = args.Contains("--axl", StringComparer.Ordinal);
            var path = args.Skip(3).FirstOrDefault(argument => !string.Equals(argument, "--axl", StringComparison.Ordinal)) ?? $"{skill.Id}-{skill.Version}.{(axl ? "axl" : "json")}";
            if (axl)
            {
                await File.WriteAllTextAsync(path, SkillAxlProjection.Format(skill, AxlFormatMode.Pretty));
            }
            else
            {
                await new JsonSkillRegistryStore(path).SaveAsync(new SkillRegistrySnapshot([skill]));
            }
            Console.WriteLine($"Exported {skill.Id}/{skill.Version} to {path}. Import remains untrusted and starts as Candidate.");
            return 0;
        }
        case "import":
        {
            var path = args.Skip(2).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.Error.WriteLine("Usage: abraxius skills import <path>");
                return 2;
            }
            var imported = await new JsonSkillRegistryStore(path).LoadAsync();
            var count = 0;
            foreach (var skill in imported.Skills ?? Array.Empty<SkillDefinition>())
            {
                var candidate = skill with { Origin = SkillOrigin.Imported, State = SkillLifecycleState.Candidate, Enabled = false, UpdatedAt = DateTimeOffset.UtcNow };
                skillRuntime.Skills.Registry.Register(candidate, replace: true);
                count++;
            }
            await skillRuntime.Skills.Registry.SaveAsync();
            Console.WriteLine($"Imported {count} Skill(s) as disabled Candidates. Validate and enable explicitly.");
            return 0;
        }
        default:
            Console.Error.WriteLine("Usage: abraxius skills [list|status|inspect|match|validate|run|enable|disable|promote|export|import]");
            return 2;
    }
}

if (command == "intelligence")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var intelligenceRuntime = AbraxiusRuntimeHost.CreateDefault(configured with
    {
        UseFileEvidence = false,
        UseFileLedger = false
    });
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
    switch (subcommand)
    {
        case "status":
            PrintIntelligenceStatus(intelligenceRuntime);
            return 0;
        case "routes":
            foreach (var candidate in intelligenceRuntime.Intelligence.RouteEngine.Candidates)
            {
                Console.WriteLine($"{candidate.Model.Gateway,-10} {candidate.Model.Tier,-10} {candidate.Model.CostClass,-10} {candidate.Model.Provider}/{candidate.Model.Route ?? candidate.Model.ModelId}");
            }

            return 0;
        case "quota":
            foreach (var candidate in intelligenceRuntime.Intelligence.RouteEngine.Candidates)
            {
                var quota = candidate.Quota;
                Console.WriteLine($"{candidate.Model.Provider,-16} remaining={quota?.RemainingTokens?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} reset={quota?.ResetAt?.ToString("u", CultureInfo.InvariantCulture) ?? "unknown"} source={quota?.Source.ToString() ?? "unknown"}");
            }

            return 0;
        case "test":
            var health = await intelligenceRuntime.Intelligence.RefreshHealthAsync();
            Console.WriteLine($"Gateway health checks completed: {health.Count}");
            Console.WriteLine("No inference request was sent. Use an explicit configured smoke harness to test a route.");
            return health.All(static item => item.Status is ProviderHealthStatus.Healthy or ProviderHealthStatus.Unknown) ? 0 : 1;
        default:
            Console.Error.WriteLine("Usage: abraxius intelligence [status|routes|quota|test]");
            return 2;
    }
}

if (command == "voice")
{
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
    switch (subcommand)
    {
        case "status":
            Console.WriteLine("STT routes:");
            foreach (var descriptor in SpeechProviderFactory.GetConfiguredSttDescriptors())
            {
                Console.WriteLine($"  {descriptor.Id} / {descriptor.Type} / {descriptor.Health.Status} / {descriptor.CostClass}");
            }
            Console.WriteLine("TTS routes:");
            foreach (var descriptor in SpeechProviderFactory.GetConfiguredTtsDescriptors())
            {
                Console.WriteLine($"  {descriptor.Id} / {descriptor.Type} / {descriptor.Health.Status} / {descriptor.CostClass}");
            }
            Console.WriteLine("Capture/playback: supplied by the platform host; no raw audio is recorded by this command.");
            return 0;
        case "test":
            var detector = new EnergyVoiceActivityDetector();
            Console.WriteLine($"VAD: {detector.Process(new AudioFrame(new byte[640], AudioFormat.NormalizedSpeech, 0, TimeSpan.Zero)).State}");
            Console.WriteLine("No live provider request was sent. Use configured hardware/provider smoke tests explicitly.");
            return 0;
        default:
            Console.Error.WriteLine("Usage: abraxius voice [status|test]");
            return 2;
    }
}

if (command == "axl")
{
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "inspect";
    switch (subcommand)
    {
        case "parse":
        case "validate":
            var input = await ReadAxlInputAsync(args.Skip(2).FirstOrDefault());
            var parsed = subcommand == "validate"
                ? AxlPipeline.ParseAndValidate(input)
                : AxlParser.Parse(input);
            Console.WriteLine($"status={parsed.Status} version={parsed.Document?.Version.ToString() ?? "unknown"}");
            foreach (var diagnostic in parsed.Diagnostics)
            {
                Console.WriteLine(diagnostic);
            }

            if (parsed.Document is not null)
            {
                Console.WriteLine($"commands={parsed.Document.Commands.Length} hash={parsed.Document.SemanticHash()}");
                foreach (var commandItem in parsed.Document.Commands)
                {
                    Console.WriteLine($"  {commandItem.Id?.ToString() ?? "-",-6} {commandItem.Name}");
                }
            }

            return parsed.IsSuccess ? 0 : 1;
        case "format":
            var formatInput = await ReadAxlInputAsync(args.Skip(2).FirstOrDefault());
            var formatParsed = AxlParser.Parse(formatInput);
            if (!formatParsed.IsSuccess || formatParsed.Document is null)
            {
                foreach (var diagnostic in formatParsed.Diagnostics)
                {
                    Console.Error.WriteLine(diagnostic);
                }

                return 1;
            }

            Console.WriteLine(AxlFormatter.Format(formatParsed.Document, args.Contains("--pretty", StringComparer.Ordinal) ? AxlFormatMode.Pretty : AxlFormatMode.Compact));
            return 0;
        case "compile":
            var compileInput = await ReadAxlInputAsync(args.Skip(2).FirstOrDefault());
            var compileParsed = AxlPipeline.ParseAndValidate(compileInput);
            if (!compileParsed.IsSuccess || compileParsed.Document is null)
            {
                foreach (var diagnostic in compileParsed.Diagnostics)
                {
                    Console.Error.WriteLine(diagnostic);
                }

                return 1;
            }

            var compilation = new AxlExecutionCompiler().Compile(compileParsed.Document, new AxlCompilationContext(ExecutionId.New(), CorrelationId.New()));
            foreach (var diagnostic in compilation.Diagnostics)
            {
                Console.WriteLine(diagnostic);
            }

            Console.WriteLine($"compiled={(compilation.IsSuccess ? "true" : "false")} intent={(compilation.Intent is not null ? "yes" : "no")} nodes={compilation.Graph?.Nodes.Length ?? 0}");
            if (compilation.Graph is not null)
            {
                foreach (var node in compilation.Graph.Nodes)
                {
                    Console.WriteLine($"  node={node.Id} kind={node.WorkKind} deps={node.Dependencies.Length} label={node.Work.GetType().Name}");
                }
            }

            Console.WriteLine("No execution was performed; compilation is policy-neutral.");
            return compilation.IsSuccess ? 0 : 1;
        case "inspect":
            var pack = AxlModelSchemaPack.Create();
            Console.WriteLine($"AXL {AxlVersion.Current}");
            Console.WriteLine($"schemas: {pack.Text}");
            Console.WriteLine("Parsing and compilation never authorize or execute capabilities.");
            return 0;
        case "benchmark":
            var sample = "axl/1 find code q=ExecutionGraph lim=20";
            var iterations = 10_000;
            var started = Stopwatch.GetTimestamp();
            for (var index = 0; index < iterations; index++)
            {
                _ = AxlParser.Parse(sample);
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            Console.WriteLine($"parse iterations={iterations} elapsed={elapsed.TotalMilliseconds:F2}ms commands/sec={iterations / elapsed.TotalSeconds:F0}");
            return 0;
        default:
            Console.Error.WriteLine("Usage: abraxius axl [parse|validate|format|compile|inspect|benchmark] [file|-] [--pretty]");
            return 2;
    }
}

if (command == "memory")
{
    var environment = PlatformEnvironmentFactory.CreateCurrent();
    IMemoryStore memoryStore = environment.Capabilities.LocalFileSystem
        ? new SqliteMemoryStore(Path.Combine(new DefaultPlatformPathProvider(environment).ApplicationDataDirectory, "memory", "knowledge.db"))
        : new InMemoryKnowledgeStore();
    await using (memoryStore)
    {
        var retriever = new HybridMemoryRetriever(memoryStore, new HashEmbeddingProvider());
        var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (subcommand)
        {
            case "status":
                var stats = await memoryStore.GetStatisticsAsync();
                Console.WriteLine($"Entries: {stats.Entries}");
                Console.WriteLine($"Chunks: {stats.Chunks}");
                Console.WriteLine($"Embeddings: {stats.Embeddings}");
                Console.WriteLine($"Knowledge edges: {stats.KnowledgeEdges}");
                Console.WriteLine($"Indexed files: {stats.IndexedFiles}");
                Console.WriteLine($"Database bytes: {stats.DatabaseBytes}");
                Console.WriteLine($"Conflicts: {stats.Conflicts}  stale: {stats.StaleEntries}");
                return 0;
            case "search":
                var searchText = string.Join(' ', args.Skip(2));
                if (string.IsNullOrWhiteSpace(searchText))
                {
                    Console.Error.WriteLine("Usage: abraxius memory search <query>");
                    return 2;
                }

                var memoryResult = await retriever.RetrieveAsync(new MemorySearchQuery(searchText));
                foreach (var hit in memoryResult.Hits)
                {
                    Console.WriteLine($"{hit.Entry.Id} {hit.Score:0.000} {hit.Entry.Kind}/{hit.Entry.ScopeKey} {hit.Entry.Title}");
                    Console.WriteLine($"  {hit.Entry.Content.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}");
                    Console.WriteLine($"  {hit.Explanation}{(hit.IsConflict ? " CONFLICT" : string.Empty)}");
                }

                if (memoryResult.Conflicts.Count > 0) Console.WriteLine($"Conflicts: {memoryResult.Conflicts.Count}");
                return 0;
            case "inspect":
                if (!MemoryId.TryParse(args.Skip(2).FirstOrDefault(), out var memoryId))
                {
                    Console.Error.WriteLine("Usage: abraxius memory inspect <memory-id>");
                    return 2;
                }

                var inspected = await memoryStore.GetAsync(memoryId);
                if (inspected is null)
                {
                    Console.Error.WriteLine("Memory entry not found.");
                    return 1;
                }

                Console.WriteLine(JsonSerializer.Serialize(inspected, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            case "rebuild":
                var root = Path.GetFullPath(args.Skip(2).FirstOrDefault() ?? Environment.CurrentDirectory);
                var projectKey = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var ingestion = await new RepositoryIngestionService(memoryStore, new HashEmbeddingProvider()).IngestAsync(new RepositoryIngestionOptions(root, projectKey), new Progress<IngestionProgress>(item => Console.WriteLine($"{item.Processed}/{item.Discovered} indexed={item.Indexed} skipped={item.Skipped} failed={item.Failed}")));
                Console.WriteLine($"Indexed {ingestion.Indexed} files in {ingestion.Duration.TotalMilliseconds:0} ms; skipped {ingestion.Skipped}; failed {ingestion.Failed}.");
                return ingestion.Failed == 0 ? 0 : 1;
            case "forget":
                if (!MemoryId.TryParse(args.Skip(2).FirstOrDefault(), out var forgottenId))
                {
                    Console.Error.WriteLine("Usage: abraxius memory forget <memory-id>");
                    return 2;
                }

                await memoryStore.ForgetAsync(forgottenId);
                Console.WriteLine($"Forgot {forgottenId}.");
                return 0;
            case "export":
                var exported = await memoryStore.ExportAsync();
                Console.WriteLine(JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            default:
                Console.Error.WriteLine("Usage: abraxius memory [status|search|inspect|rebuild|forget|export]");
                return 2;
        }
    }
}

if (command == "debrief")
{
    var configured = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
    await using var debriefRuntime = AbraxiusRuntimeHost.CreateDefault(configured with { UseFileEvidence = false, UseFileLedger = true });
    await debriefRuntime.StartAsync();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "list";
    switch (subcommand)
    {
        case "create":
        {
            var objective = ExtractDebriefObjective(args.Skip(2).ToArray());
            if (string.IsNullOrWhiteSpace(objective))
            {
                Console.Error.WriteLine("Usage: abraxius debrief create <objective> [--mode <mode>]");
                return 2;
            }

            var mode = ParseDebriefMode(args.Skip(2).ToArray());
            var session = await debriefRuntime.CreateDebriefAsync(new DebriefRequest(
                new DebriefSourceSet(ProjectKey: CurrentProjectKey(), Query: objective),
                Mode: mode,
                Objective: objective,
                GenerateAudio: false));
            var debriefResult = await debriefRuntime.Debrief.PlayAsync(session);
            Console.WriteLine($"debrief={session.Id} state={session.State} mode={mode} duration={debriefResult.Duration.TotalMilliseconds:F0}ms");
            Console.WriteLine(session.Plan.Title);
            foreach (var turn in session.Turns)
            {
                Console.WriteLine($"{turn.SpeakerName}: {turn.Text}");
                if (turn.SafeSourceRefs.Count > 0) Console.WriteLine($"  sources={string.Join(',', turn.SafeSourceRefs)}");
            }
            return debriefResult.Succeeded ? 0 : 1;
        }
        case "list":
            foreach (var snapshot in await debriefRuntime.Debrief.ListAsync())
            {
                Console.WriteLine($"{snapshot.Id} {snapshot.State} {snapshot.Plan.Mode} {snapshot.Plan.Title}");
            }
            return 0;
        case "inspect":
        {
            if (!TryParseDebriefId(args.Skip(2).FirstOrDefault(), out var id))
            {
                Console.Error.WriteLine("Usage: abraxius debrief inspect <id>");
                return 2;
            }
            var snapshot = (await debriefRuntime.Debrief.ListAsync()).FirstOrDefault(item => item.Id == id);
            if (snapshot is null)
            {
                Console.Error.WriteLine("Debrief not found.");
                return 1;
            }
            Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        case "export":
        {
            if (!TryParseDebriefId(args.Skip(2).FirstOrDefault(), out var id))
            {
                Console.Error.WriteLine("Usage: abraxius debrief export <id> [path]");
                return 2;
            }
            var session = await debriefRuntime.Debrief.RestoreAsync(id);
            if (session is null)
            {
                Console.Error.WriteLine("Debrief not found.");
                return 1;
            }
            var path = args.Skip(3).FirstOrDefault() ?? $"debrief-{id}.md";
            await using var output = File.Create(path);
            await debriefRuntime.Debrief.ExportTranscriptAsync(session, output);
            Console.WriteLine($"Exported {path}");
            return 0;
        }
        case "export-audio":
        {
            if (!TryParseDebriefId(args.Skip(2).FirstOrDefault(), out var id))
            {
                Console.Error.WriteLine("Usage: abraxius debrief export-audio <id> [path]");
                return 2;
            }
            var session = await debriefRuntime.Debrief.RestoreAsync(id);
            if (session is null)
            {
                Console.Error.WriteLine("Debrief not found.");
                return 1;
            }
            var path = args.Skip(3).FirstOrDefault() ?? $"debrief-{id}.wav";
            await using var output = File.Create(path);
            await debriefRuntime.Debrief.ExportAudioAsync(session, output);
            Console.WriteLine($"Exported {path}");
            return 0;
        }
        default:
            Console.Error.WriteLine("Usage: abraxius debrief [create <objective>|list|inspect <id>|export <id> [path]|export-audio <id> [path]]");
            return 2;
    }
}

if (command == "update")
{
    await using var updates = new VelopackUpdateService();
    var subcommand = args.Skip(1).FirstOrDefault()?.ToLowerInvariant() ?? "status";
    switch (subcommand)
    {
        case "status":
            PrintUpdateStatus(updates);
            return 0;
        case "check":
            var check = await updates.CheckAsync();
            PrintUpdateStatus(updates);
            return check.Error is null ? 0 : 1;
        case "download":
            var downloadCheck = await updates.CheckAsync();
            if (downloadCheck.Update is null)
            {
                PrintUpdateStatus(updates);
                return downloadCheck.Error is null ? 0 : 1;
            }

            var download = await updates.DownloadAsync(
                downloadCheck.Update,
                new Progress<UpdateProgress>(progress => Console.WriteLine($"{progress.Phase ?? "download"}: {progress.Percent?.ToString(CultureInfo.InvariantCulture) ?? "?"}%")));
            PrintUpdateStatus(updates);
            return download.Error is null ? 0 : 1;
        case "apply":
            var applyCheck = await updates.CheckAsync();
            if (applyCheck.Update is null)
            {
                PrintUpdateStatus(updates);
                return applyCheck.Error is null ? 0 : 1;
            }

            var downloaded = await updates.DownloadAsync(applyCheck.Update);
            if (!downloaded.IsReady)
            {
                PrintUpdateStatus(updates);
                return 1;
            }

            var applied = await updates.ApplyAsync(applyCheck.Update, UpdateApplyMode.RestartNow);
            Console.WriteLine($"Update apply: {applied.State}");
            return applied.Error is null ? 0 : 1;
        case "rollback":
            if (updates is not IUpdateRecoveryService recovery)
            {
                Console.Error.WriteLine("Rollback is unavailable for this installation.");
                return 1;
            }

            var rollback = await recovery.RollbackAsync();
            Console.WriteLine($"Rollback: {rollback.State}");
            if (rollback.Error is not null)
            {
                Console.Error.WriteLine(rollback.Error.Message);
            }

            return rollback.Error is null ? 0 : 1;
        case "channel":
            var channelName = args.Skip(2).FirstOrDefault();
            if (!Enum.TryParse<UpdateChannel>(channelName, ignoreCase: true, out var channel))
            {
                Console.Error.WriteLine("Usage: abraxius update channel [stable|beta|development]");
                return 2;
            }

            Console.WriteLine(await updates.SetChannelAsync(channel) ? $"Update channel: {channel}" : "Update channel is locked by installation policy.");
            return 0;
        case "repair":
            if (!OperatingSystem.IsLinux())
            {
                Console.WriteLine("Integration repair is currently platform-specific; Velopack owns Windows/macOS integration.");
                return 0;
            }

            var integration = await new LinuxInstallationIntegration().ReconcileAsync();
            Console.WriteLine(integration.IsHealthy ? "Linux integration repaired." : string.Join(Environment.NewLine, integration.Issues));
            return integration.IsHealthy ? 0 : 1;
        default:
            Console.Error.WriteLine("Usage: abraxius update [status|check|download|apply|rollback|channel|repair]");
            return 2;
    }
}

if (command is not "demo" and not "run")
{
    Console.Error.WriteLine("Usage: abraxius [demo|run|agents|missions|intelligence|voice|axl|memory|debrief|skills|progression|presence|notifications|needs-you|background|update]");
    return 2;
}

var dataDirectory = new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory;
var configuredOptions = RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
await using var runtime = AbraxiusRuntimeHost.CreateDefault(configuredOptions with
{
    LedgerPath = Path.Combine(dataDirectory, "events.jsonl"),
    EvidencePath = Path.Combine(dataDirectory, "evidence")
});

var subscription = runtime.Events.Subscribe(2048, lossy: false);
var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var printer = Task.Run(async () =>
{
    await foreach (var runtimeEvent in ((IAsyncEnumerable<RuntimeEvent>)subscription).ConfigureAwait(false))
    {
        Print(runtimeEvent);
        if (runtimeEvent is ExecutionCompletedEvent)
        {
            completed.TrySetResult(true);
        }
    }
});

var result = await runtime.RunDemoAsync();
await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
await subscription.DisposeAsync();
await printer.WaitAsync(TimeSpan.FromSeconds(2));

Console.WriteLine();
Console.WriteLine($"Execution {result.ExecutionId} {(result.Succeeded ? "SUCCEEDED" : "NOT SUCCESSFUL")} in {result.Elapsed.TotalMilliseconds:F0} ms");
Console.WriteLine($"Tasks: {result.Tasks.Count}; successful: {result.Tasks.Values.Count(static task => task.State == WorkState.Succeeded)}; critical-path demo branches: 3");
Console.WriteLine($"Max observed concurrency: {runtime.Metrics.Snapshot().MaxObservedConcurrency}");
return result.Succeeded ? 0 : 1;

static void Print(RuntimeEvent runtimeEvent)
{
    var task = runtimeEvent.RelatedTaskId is { } taskId ? $" task={taskId}" : string.Empty;
    var detail = runtimeEvent switch
    {
        TaskCreatedEvent created => $" {created.Label}",
        TaskQueuedEvent queued => $" queue={queued.QueueDepth}/{queued.QueueCapacity}",
        TaskStartedEvent started => $" attempt={started.Attempt}",
        TaskProgressEvent progress => $" progress={progress.Progress:P0}",
        IntelligenceRouteSelectedEvent route => $" {route.Tier} {route.Gateway}/{route.Route}",
        TaskFailedEvent failed => $" error={failed.Error.Code}",
        ExecutionCompletedEvent execution => $" elapsed={execution.Elapsed.TotalMilliseconds:F0}ms success={execution.Succeeded}",
        _ => string.Empty
    };
    Console.WriteLine($"#{runtimeEvent.Sequence,4} {runtimeEvent.Timestamp:HH:mm:ss.fff} {runtimeEvent.Kind,-20}{task}{detail}");
}

static bool TryResolveSkill(ISkillRegistry registry, string? specification, out SkillDefinition skill)
{
    skill = default!;
    if (string.IsNullOrWhiteSpace(specification)) return false;
    var separator = specification.LastIndexOf('@');
    var idText = separator > 0 ? specification[..separator] : specification;
    SkillVersion? version = null;
    if (separator > 0)
    {
        if (!SkillVersion.TryParse(specification[(separator + 1)..], out var parsedVersion)) return false;
        version = parsedVersion;
    }
    if (!SkillId.TryParse(idText, out var id)) return false;
    return registry.TryGet(id, version, out skill!);
}

static DebriefMode ParseDebriefMode(IReadOnlyList<string> arguments)
{
    var index = Array.FindIndex(arguments.ToArray(), static item => item.Equals("--mode", StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Count && Enum.TryParse<DebriefMode>(arguments[index + 1], true, out var mode) ? mode : DebriefMode.Briefing;
}

static string ExtractDebriefObjective(IReadOnlyList<string> arguments)
{
    var values = new List<string>();
    for (var index = 0; index < arguments.Count; index++)
    {
        if (arguments[index].Equals("--mode", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            continue;
        }
        values.Add(arguments[index]);
    }
    return string.Join(' ', values);
}

static string CurrentProjectKey() => Path.GetFileName(Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

static bool TryParseDebriefId(string? value, out DebriefId id)
{
    if (value is not null && Guid.TryParseExact(value, "N", out var guid))
    {
        id = new DebriefId(guid);
        return true;
    }
    id = default;
    return false;
}

static void PrintIntelligenceStatus(AbraxiusRuntimeHost runtime)
{
    var snapshot = runtime.Intelligence.Snapshot;
    Console.WriteLine($"Routing: {snapshot.Mode}");
    Console.WriteLine($"Candidates: {snapshot.CandidateCount}");
    Console.WriteLine($"Healthy gateways: {snapshot.HealthyGatewayCount}");
    Console.WriteLine($"Free: {snapshot.FreeCandidateCount}  Included: {snapshot.IncludedCandidateCount}  Paid: {snapshot.PaidCandidateCount}  Frontier: {snapshot.FrontierCandidateCount}");
    Console.WriteLine($"Last route: {snapshot.LastDecision?.ToString() ?? "none"}");
}

static void PrintUpdateStatus(VelopackUpdateService updates)
{
    Console.WriteLine($"Installation: {updates.InstallationKind}");
    Console.WriteLine($"Version: {updates.Build.ProductVersion}");
    Console.WriteLine($"Channel: {updates.Channel}");
    Console.WriteLine($"State: {updates.State}");
    Console.WriteLine($"Last checked: {updates.LastCheckedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "never"}");
    Console.WriteLine($"Available: {updates.AvailableUpdate?.Version ?? "none"}");
    var startupHealth = new StartupHealthMarker(StartupHealthMarker.DefaultPath()).Current;
    Console.WriteLine($"Startup health: {(startupHealth?.RecoveryRequired == true ? "recovery required" : "healthy/unknown")}");
    if (updates.LastError is { } error)
    {
        Console.WriteLine($"Error: {error.Code} / {error.Message}");
    }
}

static async Task<string> ReadAxlInputAsync(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || path == "-")
    {
        return await Console.In.ReadToEndAsync().ConfigureAwait(false);
    }

    return await File.ReadAllTextAsync(path).ConfigureAwait(false);
}
