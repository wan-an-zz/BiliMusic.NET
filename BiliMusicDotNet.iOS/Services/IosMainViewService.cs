using Avalonia.Controls;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;

namespace BiliMusicDotNet.iOS.Services;

/// <summary>
/// iOS 主视图：返回单视图形态的移动端界面。
/// </summary>
/// <remarks>
/// 移动端专属界面就绪后，把这里换成该平台自己的视图即可，Core 与其它平台不受影响。
/// </remarks>
public sealed class IosMainViewService : IMainViewService
{
    /// <inheritdoc />
    public Control CreateMainView(MainViewModel viewModel) => new MainView
    {
        DataContext = viewModel,
    };
}
