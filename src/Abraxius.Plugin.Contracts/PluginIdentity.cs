using System.Globalization;
using System.Text.RegularExpressions;

namespace Abraxius.Plugin.Contracts;

public readonly record struct PluginId
{
    private static readonly Regex Pattern = new("^[a-z0-9](?:[a-z0-9.-]{1,126}[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public PluginId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToLowerInvariant();
        if (!Pattern.IsMatch(Value)) throw new ArgumentException("Plugin IDs must be lowercase reverse-DNS style identifiers.", nameof(value));
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PluginVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<PluginVersion>
{
    public static PluginVersion Parse(string value) => TryParse(value, out var version) ? version : throw new FormatException($"'{value}' is not a semantic version.");
    public static bool TryParse(string? value, out PluginVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var pieces = value.Split('-', 2, StringSplitOptions.TrimEntries);
        var numbers = pieces[0].Split('.');
        if (numbers.Length != 3 || !int.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !int.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) || !int.TryParse(numbers[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch) || major < 0 || minor < 0 || patch < 0) return false;
        version = new(major, minor, patch, pieces.Length == 2 ? pieces[1] : null);
        return true;
    }
    public int CompareTo(PluginVersion other)
    {
        var result = Major.CompareTo(other.Major); if (result != 0) return result;
        result = Minor.CompareTo(other.Minor); if (result != 0) return result;
        result = Patch.CompareTo(other.Patch); if (result != 0) return result;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }
    public static bool operator <(PluginVersion left, PluginVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(PluginVersion left, PluginVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(PluginVersion left, PluginVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(PluginVersion left, PluginVersion right) => left.CompareTo(right) >= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease is null ? string.Empty : $"-{PreRelease}")}";
}

public readonly record struct PluginPackageId(Guid Value) { public static PluginPackageId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct PluginInstallationId(Guid Value) { public static PluginInstallationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct PluginInstanceId(Guid Value) { public static PluginInstanceId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct PluginHostId(Guid Value) { public static PluginHostId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct PluginPublisherId(string Value) { public override string ToString() => Value; }
public readonly record struct PluginHostSessionId(Guid Value) { public static PluginHostSessionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct PluginContributionId(string Value) { public override string ToString() => Value; }
public readonly record struct PluginApiVersion(uint Major, uint Minor = 0) { public static PluginApiVersion Current => new(1, 0); public override string ToString() => $"{Major}.{Minor}"; }
public readonly record struct PluginProtocolVersion(uint Value) { public static PluginProtocolVersion Current => new(1); public override string ToString() => Value.ToString(CultureInfo.InvariantCulture); }
