using Avalonia.Controls;
using Avalonia.Interactivity;
using MinterludeCalc.Overlay.ViewModels;

namespace MinterludeCalc.Overlay;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Start();
    }

    private void OnDismissNewPlay(object? sender, RoutedEventArgs e)
    {
        _viewModel.DismissNewPlay();
    }
}
