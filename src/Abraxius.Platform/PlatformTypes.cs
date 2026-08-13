using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Abraxius.Protocol;

namespace Abraxius.Platform;

public enum PlatformFamily
{
    Windows,
    Linux,
    MacOs,
    Android,
    Ios,
    Browser,
    EmbeddedLinux,
    Unknown
}

public enum RuntimeExecutionMode
{
    LocalFull,
    LocalConstrained,
    Remote,
    Hybrid
}

public enum DeviceClass
{
    Workstation,
    Laptop,
    Tablet,
    Phone,
    Embedded,
    Browser,
    Server,
    Unknown
}

public enum HardwareAccelerationClass
{
    Unknown,
    Software,
    HardwareAccelerated,
    DedicatedGpu
}

public enum PerformanceProfile
{
    Maximum,
    Balanced,
    Efficiency,
    Automatic
}

public enum PowerPreference
{
    Performance,
    Balanced,
    Efficiency
}

public enum PowerSource
{
    Ac,
    Battery,
    LowBattery,
    ThermalLimited,
    Unknown
}

public enum MemoryPressureLevel
{
    Normal,
    Elevated,
    Critical
}

public enum CapabilityAvailability
{
    Available,
    Unavailable,
    PermissionRequired,
    Restricted,
    RemoteOnly
}

public enum PlatformErrorCode
{
    CapabilityUnavailable,
    PlatformNotSupported,
    PermissionRequired,
    RemoteHostUnavailable,
    PlatformServiceUnavailable,
    UnsupportedArchitecture,
    InvalidReference,
    ConnectionFailed,
    ProtocolMismatch
}

public static class PlatformCapabilities
{
    public static readonly CapabilityId FileSystem = new("platform.filesystem");
    public static readonly CapabilityId ProcessExecution = new("platform.process");
    public static readonly CapabilityId Network = new("platform.network");
    public static readonly CapabilityId SecureStorage = new("platform.secure-storage");
    public static readonly CapabilityId LocalModelInference = new("platform.local-model");
    public static readonly CapabilityId HardwareAcceleration = new("platform.hardware-rendering");
    public static readonly CapabilityId DesktopWindowing = new("platform.desktop-window");
    public static readonly CapabilityId Notifications = new("platform.notifications");
    public static readonly CapabilityId SystemTray = new("platform.system-tray");
    public static readonly CapabilityId PersistentBackground = new("platform.background.persistent");
    public static readonly CapabilityId DeepLinkActivation = new("platform.activation.deep-link");
    public static readonly CapabilityId Clipboard = new("platform.clipboard");
    public static readonly CapabilityId TouchInput = new("platform.touch");
    public static readonly CapabilityId Git = new("git");
    public static readonly CapabilityId LocalLattice = new("lattice.local");
    public static readonly CapabilityId MicrophoneCapture = new("platform.audio.microphone");
    public static readonly CapabilityId AudioPlayback = new("platform.audio.playback");
    public static readonly CapabilityId LocalVoiceActivityDetection = new("platform.audio.vad.local");
    public static readonly CapabilityId LocalSpeechToText = new("platform.audio.stt.local");
    public static readonly CapabilityId LocalTextToSpeech = new("platform.audio.tts.local");
    public static readonly CapabilityId WakeWord = new("platform.audio.wake-word");
    public static readonly CapabilityId EchoCancellation = new("platform.audio.echo-cancellation");
}

public sealed record PlatformCapability(
    CapabilityId Id,
    CapabilityAvailability Availability,
    string? Version = null,
    IReadOnlyDictionary<string, string>? Constraints = null);

public sealed class PlatformCapabilitySet
{
    private readonly FrozenDictionary<CapabilityId, PlatformCapability> _capabilities;

    public PlatformCapabilitySet(IEnumerable<PlatformCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities
            .GroupBy(static capability => capability.Id)
            .ToFrozenDictionary(static group => group.Key, static group => group.Last());
    }

    public IReadOnlyCollection<PlatformCapability> Values => _capabilities.Values;

    public bool Contains(CapabilityId capability) => _capabilities.ContainsKey(capability);

    public PlatformCapability Get(CapabilityId capability) =>
        _capabilities.TryGetValue(capability, out var value)
            ? value
            : new PlatformCapability(capability, CapabilityAvailability.Unavailable);

    public CapabilityAvailability GetAvailability(CapabilityId capability) => Get(capability).Availability;

    public bool IsAvailable(CapabilityId capability) => GetAvailability(capability) == CapabilityAvailability.Available;

    public bool LocalFileSystem => IsAvailable(PlatformCapabilities.FileSystem);
    public bool ProcessExecution => IsAvailable(PlatformCapabilities.ProcessExecution);
    public bool LocalNetworking => IsAvailable(PlatformCapabilities.Network);
    public bool SecureStorage => IsAvailable(PlatformCapabilities.SecureStorage);
    public bool LocalModelInference => IsAvailable(PlatformCapabilities.LocalModelInference);
    public bool HardwareAcceleration => IsAvailable(PlatformCapabilities.HardwareAcceleration);
    public bool DesktopWindowing => IsAvailable(PlatformCapabilities.DesktopWindowing);
    public bool Notifications => IsAvailable(PlatformCapabilities.Notifications);
    public bool SystemTray => IsAvailable(PlatformCapabilities.SystemTray);
    public bool PersistentBackground => IsAvailable(PlatformCapabilities.PersistentBackground);
    public bool DeepLinkActivation => IsAvailable(PlatformCapabilities.DeepLinkActivation);
    public bool Clipboard => IsAvailable(PlatformCapabilities.Clipboard);
    public bool MicrophoneCapture => IsAvailable(PlatformCapabilities.MicrophoneCapture);
    public bool AudioPlayback => IsAvailable(PlatformCapabilities.AudioPlayback);
    public bool LocalVoiceActivityDetection => IsAvailable(PlatformCapabilities.LocalVoiceActivityDetection);
    public bool LocalSpeechToText => IsAvailable(PlatformCapabilities.LocalSpeechToText);
    public bool LocalTextToSpeech => IsAvailable(PlatformCapabilities.LocalTextToSpeech);
    public bool WakeWord => IsAvailable(PlatformCapabilities.WakeWord);
    public bool EchoCancellation => IsAvailable(PlatformCapabilities.EchoCancellation);
}

public sealed record PlatformDescriptor(
    PlatformFamily Family,
    string OperatingSystem,
    string OperatingSystemVersion,
    Architecture Architecture,
    string RuntimeDescription,
    bool Is64BitProcess,
    bool IsEmulated = false);

public sealed record DeviceProfile
{
    public DeviceProfile(
        DeviceClass @class,
        int logicalProcessorCount,
        ulong? approximateMemoryBytes,
        bool batteryPowered,
        bool touchPrimary,
        HardwareAccelerationClass graphics,
        PowerSource powerSource = PowerSource.Unknown,
        MemoryPressureLevel memoryPressure = MemoryPressureLevel.Normal,
        PerformanceProfile performanceProfile = PerformanceProfile.Automatic)
    {
        Class = @class;
        LogicalProcessorCount = Math.Max(1, logicalProcessorCount);
        ApproximateMemoryBytes = approximateMemoryBytes;
        BatteryPowered = batteryPowered;
        TouchPrimary = touchPrimary;
        Graphics = graphics;
        PowerSource = powerSource;
        MemoryPressure = memoryPressure;
        PerformanceProfile = performanceProfile;
    }

    public DeviceClass Class { get; }
    public int LogicalProcessorCount { get; }
    public ulong? ApproximateMemoryBytes { get; }
    public bool BatteryPowered { get; }
    public bool TouchPrimary { get; }
    public HardwareAccelerationClass Graphics { get; }
    public PowerSource PowerSource { get; }
    public MemoryPressureLevel MemoryPressure { get; }
    public PerformanceProfile PerformanceProfile { get; }
}

public sealed record ExecutionBudget
{
    public ExecutionBudget(
        int maximumConcurrency,
        int cpuWorkerLimit,
        int modelConcurrency,
        int toolConcurrency,
        bool allowBackgroundActivity,
        MemoryPressureLevel memoryPressure,
        PowerPreference powerPreference,
        PerformanceProfile performanceProfile,
        bool preferRemote)
    {
        MaximumConcurrency = Math.Max(1, maximumConcurrency);
        CpuWorkerLimit = Math.Max(1, cpuWorkerLimit);
        ModelConcurrency = Math.Max(0, modelConcurrency);
        ToolConcurrency = Math.Max(0, toolConcurrency);
        AllowBackgroundActivity = allowBackgroundActivity;
        MemoryPressure = memoryPressure;
        PowerPreference = powerPreference;
        PerformanceProfile = performanceProfile;
        PreferRemote = preferRemote;
    }

    public int MaximumConcurrency { get; }
    public int CpuWorkerLimit { get; }
    public int ModelConcurrency { get; }
    public int ToolConcurrency { get; }
    public bool AllowBackgroundActivity { get; }
    public MemoryPressureLevel MemoryPressure { get; }
    public PowerPreference PowerPreference { get; }
    public PerformanceProfile PerformanceProfile { get; }
    public bool PreferRemote { get; }
}

public static class ExecutionBudgetFactory
{
    public static ExecutionBudget Create(DeviceProfile device, PlatformCapabilitySet capabilities)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(capabilities);

        var conservative = device.Class is DeviceClass.Phone or DeviceClass.Tablet or DeviceClass.Browser ||
            device.PowerSource is PowerSource.Battery or PowerSource.LowBattery or PowerSource.ThermalLimited;
        var cpuLimit = conservative
            ? Math.Min(device.LogicalProcessorCount, 4)
            : Math.Min(device.LogicalProcessorCount, 16);
        var maximum = conservative ? Math.Max(1, cpuLimit) : Math.Max(2, cpuLimit);
        var model = capabilities.IsAvailable(PlatformCapabilities.LocalModelInference)
            ? conservative ? 1 : Math.Min(4, maximum)
            : 0;
        var tools = capabilities.ProcessExecution || capabilities.LocalFileSystem
            ? conservative ? 4 : Math.Min(32, Math.Max(4, maximum * 2))
            : 0;

        return new ExecutionBudget(
            maximum,
            cpuLimit,
            model,
            tools,
            !conservative && device.MemoryPressure != MemoryPressureLevel.Critical,
            device.MemoryPressure,
            device.PowerSource is PowerSource.Battery or PowerSource.LowBattery ? PowerPreference.Efficiency :
                conservative ? PowerPreference.Balanced : PowerPreference.Performance,
            device.PerformanceProfile,
            device.Class == DeviceClass.Browser || !capabilities.IsAvailable(PlatformCapabilities.LocalLattice));
    }
}

public interface IPlatformEnvironment
{
    PlatformDescriptor Platform { get; }
    PlatformCapabilitySet Capabilities { get; }
    DeviceProfile Device { get; }
    RuntimeExecutionMode ExecutionMode { get; }
    ExecutionBudget Budget { get; }
}

public sealed record PlatformEnvironment(
    PlatformDescriptor Platform,
    PlatformCapabilitySet Capabilities,
    DeviceProfile Device,
    RuntimeExecutionMode ExecutionMode,
    ExecutionBudget Budget) : IPlatformEnvironment;

public static class PlatformEnvironmentFactory
{
    public static IPlatformEnvironment CreateCurrent()
    {
        var platform = DetectPlatform();
        var device = DetectDevice(platform);
        var capabilities = DetectCapabilities(platform, device);
        var mode = platform.Family switch
        {
            PlatformFamily.Browser => RuntimeExecutionMode.Remote,
            PlatformFamily.Android or PlatformFamily.Ios => RuntimeExecutionMode.Hybrid,
            PlatformFamily.EmbeddedLinux => RuntimeExecutionMode.LocalConstrained,
            _ => RuntimeExecutionMode.LocalFull
        };
        var budget = ExecutionBudgetFactory.Create(device, capabilities);
        return new PlatformEnvironment(platform, capabilities, device, mode, budget);
    }

    public static PlatformEnvironment Create(
        PlatformDescriptor platform,
        DeviceProfile device,
        PlatformCapabilitySet capabilities,
        RuntimeExecutionMode executionMode)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(capabilities);
        return new PlatformEnvironment(platform, capabilities, device, executionMode, ExecutionBudgetFactory.Create(device, capabilities));
    }

    private static PlatformDescriptor DetectPlatform()
    {
        var family = OperatingSystem.IsWindows() ? PlatformFamily.Windows :
            OperatingSystem.IsMacOS() ? PlatformFamily.MacOs :
            OperatingSystem.IsAndroid() ? PlatformFamily.Android :
            OperatingSystem.IsIOS() ? PlatformFamily.Ios :
            OperatingSystem.IsBrowser() ? PlatformFamily.Browser :
            OperatingSystem.IsLinux() ? IsEmbeddedLinux() ? PlatformFamily.EmbeddedLinux : PlatformFamily.Linux :
            PlatformFamily.Unknown;

        return new PlatformDescriptor(
            family,
            RuntimeInformation.OSDescription,
            Environment.OSVersion.VersionString,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.FrameworkDescription,
            Environment.Is64BitProcess);
    }

    private static DeviceProfile DetectDevice(PlatformDescriptor platform)
    {
        var @class = platform.Family switch
        {
            PlatformFamily.Android => DeviceClass.Phone,
            PlatformFamily.Ios => DeviceClass.Phone,
            PlatformFamily.Browser => DeviceClass.Browser,
            PlatformFamily.EmbeddedLinux => DeviceClass.Embedded,
            PlatformFamily.Windows or PlatformFamily.Linux or PlatformFamily.MacOs => DeviceClass.Workstation,
            _ => DeviceClass.Unknown
        };
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var graphics = Environment.GetEnvironmentVariable("ABRAXIUS_HARDWARE_ACCELERATION") switch
        {
            "1" or "true" => HardwareAccelerationClass.HardwareAccelerated,
            "0" or "false" => HardwareAccelerationClass.Software,
            _ => HardwareAccelerationClass.Unknown
        };
        return new DeviceProfile(
            @class,
            Environment.ProcessorCount,
            memory > 0 ? (ulong)memory : null,
            @class is DeviceClass.Phone or DeviceClass.Tablet,
            @class is DeviceClass.Phone or DeviceClass.Tablet or DeviceClass.Embedded,
            graphics,
            @class is DeviceClass.Phone or DeviceClass.Tablet ? PowerSource.Battery : PowerSource.Unknown);
    }

    private static PlatformCapabilitySet DetectCapabilities(PlatformDescriptor platform, DeviceProfile device)
    {
        var desktop = platform.Family is PlatformFamily.Windows or PlatformFamily.Linux or PlatformFamily.MacOs;
        var embedded = platform.Family == PlatformFamily.EmbeddedLinux;
        var mobile = platform.Family is PlatformFamily.Android or PlatformFamily.Ios;
        var browser = platform.Family == PlatformFamily.Browser;
        var process = desktop || embedded;
        var pulseAudio = platform.Family is PlatformFamily.Linux or PlatformFamily.EmbeddedLinux &&
            IsExecutableOnPath("parec") && IsExecutableOnPath("pacat");
        var filesystem = desktop || embedded || mobile;
        var capabilities = new List<PlatformCapability>
        {
            new(PlatformCapabilities.FileSystem, filesystem ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.ProcessExecution, process ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.Network, browser || desktop || embedded || mobile ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.SecureStorage, browser ? CapabilityAvailability.Restricted : mobile || desktop || embedded ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.LocalModelInference, browser || mobile ? CapabilityAvailability.Restricted : CapabilityAvailability.Available),
            new(PlatformCapabilities.HardwareAcceleration, browser || mobile || embedded ? CapabilityAvailability.Available : device.Graphics == HardwareAccelerationClass.Software ? CapabilityAvailability.Restricted : CapabilityAvailability.Available),
            new(PlatformCapabilities.DesktopWindowing, desktop ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.Notifications, browser || mobile || desktop ? CapabilityAvailability.Available : CapabilityAvailability.Restricted),
            new(PlatformCapabilities.SystemTray, desktop ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.PersistentBackground, desktop || embedded ? CapabilityAvailability.Available : mobile || browser ? CapabilityAvailability.Restricted : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.DeepLinkActivation, browser || mobile || desktop ? CapabilityAvailability.Available : CapabilityAvailability.Restricted),
            new(PlatformCapabilities.Clipboard, browser || mobile || desktop ? CapabilityAvailability.Available : CapabilityAvailability.Restricted),
            new(PlatformCapabilities.TouchInput, mobile || embedded ? CapabilityAvailability.Available : CapabilityAvailability.Unavailable),
            new(PlatformCapabilities.LocalLattice, process ? CapabilityAvailability.Available : CapabilityAvailability.RemoteOnly)
        };

        // Audio capability is deliberately conservative. The platform host must register an
        // actual capture/playback backend before the voice runtime advertises local audio.
        // Local VAD remains available on runtimes where the voice core has a managed detector;
        // native STT/TTS and wake-word support are adapter/model dependent.
        capabilities.Add(new PlatformCapability(
            PlatformCapabilities.MicrophoneCapture,
            pulseAudio ? CapabilityAvailability.Available : browser ? CapabilityAvailability.PermissionRequired : CapabilityAvailability.Unavailable));
        capabilities.Add(new PlatformCapability(
            PlatformCapabilities.AudioPlayback,
            pulseAudio ? CapabilityAvailability.Available : browser ? CapabilityAvailability.PermissionRequired : CapabilityAvailability.Unavailable));
        capabilities.Add(new PlatformCapability(
            PlatformCapabilities.LocalVoiceActivityDetection,
            CapabilityAvailability.Available));
        capabilities.Add(new PlatformCapability(
            PlatformCapabilities.LocalSpeechToText,
            CapabilityAvailability.Unavailable));
        capabilities.Add(new PlatformCapability(
            PlatformCapabilities.LocalTextToSpeech,
            CapabilityAvailability.Unavailable));
        capabilities.Add(new PlatformCapability(
            PlatformCapabilities.WakeWord,
            CapabilityAvailability.Unavailable));
        capabilities.Add(new PlatformCapability(
            PlatformCapabilities.EchoCancellation,
            mobile || browser ? CapabilityAvailability.PermissionRequired : CapabilityAvailability.Unavailable));

        if (process && IsExecutableOnPath("git"))
        {
            capabilities.Add(new PlatformCapability(PlatformCapabilities.Git, CapabilityAvailability.Available));
        }

        return new PlatformCapabilitySet(capabilities);
    }

    private static bool IsEmbeddedLinux() =>
        string.Equals(Environment.GetEnvironmentVariable("ABRAXIUS_PLATFORM"), "embedded-linux", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("ABRAXIUS_EMBEDDED"), "1", StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutableOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
