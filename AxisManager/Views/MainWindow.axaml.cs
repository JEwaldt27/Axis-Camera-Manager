using Avalonia.Controls;
using Avalonia.Interactivity;
using AxisManager.Models;
using AxisManager.ViewModels;

namespace AxisManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Clipboard helpers for stream buttons (called from AXAML Click events)
    private async void OnCopyRtspClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
            await CopyTextAsync(url);
    }

    private async void OnCopyHttpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
            await CopyTextAsync(url);
    }

    private async Task CopyTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
            if (DataContext is MainViewModel vm)
                vm.StatusText = $"Copied: {text}";
        }
    }
}
