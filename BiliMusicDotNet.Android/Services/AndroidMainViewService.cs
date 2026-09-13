using Avalonia.Controls;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;

namespace BiliMusicDotNet.Android.Services;

/// <summary>
/// Android 主视图
/// </summary>
public sealed class AndroidMainViewService : IMainViewService
{
    /// <inheritdoc />
    public Control CreateMainView(MainViewModel viewModel) => new MainView
    {
        DataContext = viewModel,
    };
}
