using System.Collections.Immutable;
using System.Windows.Input;

namespace Abraxius.App;

public sealed record CommandContext(MainViewModel ViewModel, UiTaskSnapshot? SelectedTask);

/// <summary>One command definition shared by palette, keyboard, buttons, and context surfaces.</summary>
public sealed class CommandDescriptor
{
    private readonly Func<CommandContext, ValueTask> _execute;

    public CommandDescriptor(
        string id,
        string title,
        string description,
        string category,
        string shortcut,
        Func<CommandContext, ValueTask> execute)
    {
        Id = id;
        Title = title;
        Description = description;
        Category = category;
        Shortcut = shortcut;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Category { get; }
    public string Shortcut { get; }
    public ValueTask ExecuteAsync(MainViewModel viewModel) => _execute(new CommandContext(viewModel, viewModel.SelectedTask));

    public ICommand CreateCommand(MainViewModel viewModel) =>
        new AsyncRelayCommand(() => ExecuteAsync(viewModel).AsTask());
}

public sealed record CommandItemViewModel(CommandDescriptor Descriptor, ICommand Command)
{
    public string Title => Descriptor.Title;
    public string Description => Descriptor.Description;
    public string Category => Descriptor.Category;
    public string Shortcut => Descriptor.Shortcut;
}

public sealed class CommandRegistry
{
    private readonly List<CommandDescriptor> _commands = [];

    public IReadOnlyList<CommandDescriptor> Commands => _commands;

    public void Register(CommandDescriptor command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_commands.Any(existing => string.Equals(existing.Id, command.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Command '{command.Id}' is already registered.");
        }

        _commands.Add(command);
    }

    public ImmutableArray<CommandDescriptor> Search(string? query, bool contextOnly = false)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var selected = _commands.Where(command => !contextOnly || command.Category is "Task" or "Execution");
        if (normalized.Length == 0)
        {
            return selected.Take(12).ToImmutableArray();
        }

        return selected
            .Select(command => (Command: command, Score: Score(command, normalized)))
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Command.Title, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(static item => item.Command)
            .ToImmutableArray();
    }

    private static int Score(CommandDescriptor command, string query)
    {
        if (command.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100 - command.Title.Length;
        }

        if (command.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 70 - command.Id.Length;
        }

        if (command.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 30 - command.Description.Length;
        }

        var score = 0;
        foreach (var character in query)
        {
            if (command.Title.Contains(character, StringComparison.OrdinalIgnoreCase))
            {
                score++;
            }
        }

        return score == query.Length ? score : 0;
    }
}

public enum MissionViewMode
{
    Graph,
    Lanes,
    Agents,
    Activity
}

public enum RailDestination
{
    Mission,
    Chat,
    Agents,
    Projects,
    Terminal,
    Memory,
    Debrief,
    Skills,
    Progression,
    Artifacts,
    Evaluation,
    Fabric,
    Compute,
    Extensions,
    NeedsYou,
    Security,
    Diagnostics,
    Settings
}

public enum UiDensity
{
    Compact,
    Comfortable,
    Touch
}

public enum ActivityFilter
{
    All,
    Agents,
    Tools,
    Terminal,
    Changes,
    Verification,
    Warnings,
    Errors
}

public sealed record UiLayoutPreferences(
    bool InspectorVisible = true,
    bool ActivityVisible = true,
    bool RailExpanded = false,
    MissionViewMode MissionView = MissionViewMode.Graph,
    UiDensity Density = UiDensity.Compact,
    bool ReducedMotion = false);
