using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using BiliMusicDotNet.Controls;
using CommunityToolkit.Mvvm.Input;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 设置页的 SliverPage 派生类：内置返回按钮应执行绑定命令。
/// </summary>
public sealed class SettingSliverPageTests
{
    [AvaloniaFact]
    public void 返回按钮执行BackCommand()
    {
        var executed = 0;
        var page = new SettingSliverPage
        {
            BackCommand = new RelayCommand(() => executed++),
        };

        page.GoBack(null, new RoutedEventArgs());

        Assert.Equal(1, executed);
    }

    [AvaloniaFact]
    public void 没有BackCommand时返回不抛异常()
    {
        var page = new SettingSliverPage();

        page.GoBack(null, new RoutedEventArgs());
    }
}
