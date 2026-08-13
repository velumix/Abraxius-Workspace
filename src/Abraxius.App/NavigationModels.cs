using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Abraxius.App;

/// <summary>One source of truth for the workstation's primary navigation rail.</summary>
public sealed class UiNavigationItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _badgeText = string.Empty;

    public UiNavigationItem(
        string label,
        RailDestination? destination,
        string group,
        string iconData,
        ICommand command,
        string? description = null)
    {
        Label = label;
        Destination = destination;
        Group = group;
        IconData = iconData;
        Command = command;
        Description = description ?? label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; }
    public string Group { get; }
    public string Description { get; }
    public RailDestination? Destination { get; }
    public string IconData { get; }
    public ICommand Command { get; }
    public bool IsSelected { get => _isSelected; private set => SetProperty(ref _isSelected, value); }
    public string BadgeText { get => _badgeText; set => SetProperty(ref _badgeText, value); }
    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeText);

    public void SetSelected(bool selected) => IsSelected = selected;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed record UiNavigationGroup(string Name, IReadOnlyList<UiNavigationItem> Items);

/// <summary>Vector icons used by the rail; no font glyphs or emoji are used for navigation.</summary>
public static class NavigationIcons
{
    public static string Mission { get; } = "M12 2 L14 8 L20 8 L15 12 L17 18 L12 14 L7 18 L9 12 L4 8 L10 8 Z";
    public static string Chat { get; } = "M3 4 L21 4 L21 16 L14 16 L10 20 L10 16 L3 16 Z";
    public static string Agents { get; } = "M3 5 L9 5 L9 11 L3 11 Z M15 5 L21 5 L21 11 L15 11 Z M9 14 L15 14 L15 20 L9 20 Z";
    public static string Artifacts { get; } = "M4 4 L20 4 L20 18 L4 18 Z M8 8 L16 8 M8 12 L16 12";
    public static string Memory { get; } = "M4 5 C4 2 20 2 20 5 L20 18 C20 21 4 21 4 18 Z M4 5 C4 8 20 8 20 5 M4 11 C4 14 20 14 20 11";
    public static string Skills { get; } = "M12 3 L14 8 L19 8 L15 11 L17 17 L12 14 L7 17 L9 11 L5 8 L10 8 Z M4 19 L20 19";
    public static string Debrief { get; } = "M4 4 L20 4 L20 16 L14 16 L10 20 L10 16 L4 16 Z M8 8 L16 8 M8 12 L14 12";
    public static string Evaluation { get; } = "M4 18 L8 12 L11 15 L16 7 L20 10 M4 20 L20 20";
    public static string Terminal { get; } = "M4 5 L20 5 L20 19 L4 19 Z M7 9 L10 12 L7 15 M12 15 L17 15";
    public static string Diagnostics { get; } = "M12 3 L20 7 L20 17 L12 21 L4 17 L4 7 Z M8 12 L11 15 L16 9";
    public static string Fabric { get; } = "M5 5 L19 5 L19 19 L5 19 Z M2 12 L5 12 M19 12 L22 12 M12 2 L12 5 M12 19 L12 22";
    public static string Compute { get; } = "M4 4 L20 4 L20 20 L4 20 Z M8 8 L16 8 L16 16 L8 16 Z";
    public static string Extensions { get; } = "M10 4 L14 4 L14 10 L20 10 L20 14 L14 14 L14 20 L10 20 L10 14 L4 14 L4 10 L10 10 Z";
    public static string DesignStudio { get; } = "M4 5 L20 5 L20 17 L14 17 L12 20 L10 17 L4 17 Z M8 9 L16 9 M8 13 L13 13";
    public static string NeedsYou { get; } = "M12 3 L21 19 L3 19 Z M12 9 L12 14 M12 17 L12 17";
    public static string Security { get; } = "M12 3 L20 6 L20 12 C20 17 16 20 12 21 C8 20 4 17 4 12 L4 6 Z M8 12 L11 15 L16 9";
    public static string Commands { get; } = "M4 5 L20 5 L20 19 L4 19 Z M8 9 L16 9 M8 13 L13 13";
    public static string Settings { get; } = "M12 3 L14 6 L18 6 L18 10 L21 12 L18 14 L18 18 L14 18 L12 21 L10 18 L6 18 L6 14 L3 12 L6 10 L6 6 L10 6 Z M10 10 A3 3 0 1 0 14 10 A3 3 0 1 0 10 10";
    public static string RailToggle { get; } = "M4 5 L20 5 L20 19 L4 19 Z M8 9 L12 12 L8 15 M16 9 L12 12 L16 15";
}
