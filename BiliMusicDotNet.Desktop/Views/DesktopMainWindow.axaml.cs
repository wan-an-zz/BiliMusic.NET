using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BiliMusicDotNet.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.Desktop.Views;

/// <summary>
/// 桌面端主窗口
/// </summary>
public partial class DesktopMainWindow : Window
{
    public DesktopMainWindow()
    {
        InitializeComponent();

        DataContext = App.Services!.GetRequiredService<MainViewModel>();
    }

    /// <summary>在标题栏空白处按住左键拖动窗口。</summary>
    private void OnDragAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        if (!point.Properties.IsLeftButtonPressed)
            return;

        // 双击标题栏切换最大化，与系统窗口行为一致。
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
