using Avalonia.Controls;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;

namespace BiliMusicDotNet.Browser.Services;

/// <summary>
/// 浏览器主视图：返回单视图形态的 Web 界面。
/// </summary>
/// <remarks>
/// Web 专属界面就绪后，把这里换成该平台自己的视图即可，Core 与其它平台不受影响。
/// </remarks>
public sealed class BrowserMainViewService : IMainViewService
{
    /// <inheritdoc />
    public Control CreateMainView(MainViewModel viewModel) => new MainView
    {
        DataContext = viewModel,
    };
}
