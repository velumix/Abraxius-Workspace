using Avalonia.Controls;
using Avalonia.Input;
using Abraxius.Protocol;

namespace Abraxius.App;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    public MainView(MainViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private MainViewModel ViewModel => DataContext as MainViewModel
        ?? throw new InvalidOperationException("MainView requires a MainViewModel data context.");

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.AttachDesignCaptureRoot(this);
        }
    }

    private void OnTaskSelected(object? sender, TaskId taskId) => ViewModel.SelectTask(taskId);

    private void OnMemorySearchKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is Avalonia.Input.Key.Enter)
        {
            ViewModel.Memory.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var scale = VisualRoot is TopLevel topLevel ? topLevel.RenderScaling : 1;
        ViewModel.UpdateViewport(e.NewSize.Width, e.NewSize.Height, scale);
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        var commandModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (commandModifier && e.Key == Key.K)
        {
            ViewModel.OpenCommandPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.IsCommandPaletteOpen)
        {
            ViewModel.CloseCommandPalette();
            e.Handled = true;
            return;
        }

        if (!commandModifier)
        {
            return;
        }

        var view = e.Key switch
        {
            Key.D1 => MissionViewMode.Graph,
            Key.D2 => MissionViewMode.Lanes,
            Key.D3 => MissionViewMode.Agents,
            Key.D4 => MissionViewMode.Activity,
            _ => (MissionViewMode?)null
        };
        if (view is { } selectedView)
        {
            ViewModel.SetMissionView(selectedView);
            e.Handled = true;
        }
    }

    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        var commandModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (e.Key == Key.Enter && commandModifier)
        {
            ViewModel.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel.Terminal.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.CloseCommandPalette();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && ViewModel.CommandResults.Count > 0)
        {
            ViewModel.ExecutePaletteCommand.Execute(ViewModel.CommandResults[0]);
            e.Handled = true;
        }
    }
}
