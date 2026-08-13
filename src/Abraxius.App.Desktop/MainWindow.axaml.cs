using Avalonia.Controls;

namespace Abraxius.App.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
        : this(new Abraxius.App.MainViewModel(Abraxius.Runtime.AbraxiusRuntimeHost.CreateDefault(new Abraxius.Runtime.RuntimeHostOptions(UseFileEvidence: false, UseFileLedger: false))))
    {
    }

    public MainWindow(Abraxius.App.MainViewModel viewModel)
    {
        InitializeComponent();
        ((Abraxius.App.MainView)Content!).DataContext = viewModel;
    }
}
