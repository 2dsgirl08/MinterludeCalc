using Avalonia.Controls;
using Avalonia.Input;
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

    /// <summary>
    /// The window has no system chrome (SystemDecorations="None"), so the title
    /// bar has to move it itself. Left button only - a right-click drag would
    /// otherwise steal the context menu gesture.
    /// </summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnSelectTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tab })
            _viewModel.SelectTab(tab);
    }

    private void OnCreateProfile(object? sender, RoutedEventArgs e)
    {
        _viewModel.CreateProfile();
    }

    private void OnDeleteProfile(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteActiveProfile();
    }

    /// <summary>
    /// The plot canvas reports pointer position in its own coordinates, which
    /// are exactly the coordinates the curve was built in - so the x value can
    /// go straight to the view model without any transform.
    /// </summary>
    private void OnGraphPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Control canvas)
            _viewModel.UpdateGraphHover(e.GetPosition(canvas).X);
    }

    private void OnGraphPointerExited(object? sender, PointerEventArgs e)
    {
        _viewModel.ClearGraphHover();
    }

    private void OnDismissNewPlay(object? sender, RoutedEventArgs e)
    {
        _viewModel.DismissNewPlay();
    }
}
