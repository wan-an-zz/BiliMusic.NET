using Avalonia.Controls;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;

namespace BiliMusicDotNet.Desktop.Services;

/// <summary>
/// 桌面端主视图
/// </summary>
public sealed class DesktopMainViewService : IMainViewService
{
    /// <inheritdoc />
    public Control CreateMainView(MainViewModel viewModel) => new MainWindow
    {
        DataContext = viewModel,
    };
}
