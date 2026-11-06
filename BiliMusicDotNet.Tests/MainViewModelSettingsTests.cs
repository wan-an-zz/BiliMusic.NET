using Avalonia.Headless.XUnit;
using BiliMusicDotNet.Tests.Support;
using BiliMusicDotNet.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 主视图模型的设置页状态：虚拟导航项、打开/关闭与事件转发。
/// </summary>
public sealed class MainViewModelSettingsTests
{
    [AvaloniaFact]
    public void 构造时SelectedNavItem为空()
    {
        var main = CreateMain(out _, out _, out _);

        Assert.Null(main.SelectedNavItem);
    }

    [AvaloniaFact]
    public void 设置虚拟项不在AllViews()
    {
        var main = CreateMain(out _, out _, out _);

        Assert.DoesNotContain(main.AllViews, item => ReferenceEquals(item, main.SettingsNavItem));
        Assert.Equal("设置", main.SettingsNavItem.Label);
    }

    [AvaloniaFact]
    public void 打开设置后SelectedNavItem为虚拟项()
    {
        var main = CreateMain(out _, out _, out _);

        main.OpenSettings();

        Assert.Same(main.SettingsNavItem, main.SelectedNavItem);
    }

    [AvaloniaFact]
    public void 打开设置后IsSettingsOpen为真()
    {
        var main = CreateMain(out _, out _, out _);

        main.OpenSettings();

        Assert.True(main.IsSettingsOpen);
    }

    [AvaloniaFact]
    public void 关闭设置恢复打开前的页面()
    {
        var main = CreateMain(out _, out _, out _);
        var previous = main.AllViews[0];
        main.SelectedNavItem = previous;
        main.OpenSettings();

        main.CloseSettings();

        Assert.False(main.IsSettingsOpen);
        Assert.Same(previous, main.SelectedNavItem);
    }

    [AvaloniaFact]
    public void 点击普通导航项关闭设置()
    {
        var main = CreateMain(out _, out _, out _);
        main.OpenSettings();

        var target = main.AllViews[0];
        main.SelectedNavItem = target;

        Assert.False(main.IsSettingsOpen);
        Assert.Same(target, main.SelectedNavItem);
    }

    [AvaloniaFact]
    public void 标题栏切换命令可开可关()
    {
        var main = CreateMain(out _, out _, out _);

        main.ToggleSettingsCommand.Execute(null);
        Assert.True(main.IsSettingsOpen);

        main.ToggleSettingsCommand.Execute(null);
        Assert.False(main.IsSettingsOpen);
        Assert.Null(main.SelectedNavItem);
    }

    [AvaloniaFact]
    public void 关于页请求打开设置()
    {
        var main = CreateMain(out var about, out _, out _);

        about.OpenSettingsCommand.Execute(null);

        Assert.True(main.IsSettingsOpen);
        Assert.Same(main.SettingsNavItem, main.SelectedNavItem);
    }

    [AvaloniaFact]
    public void 设置页内容为SettingViewModel()
    {
        var main = CreateMain(out _, out var settings, out _);

        Assert.Same(settings, main.Settings);
        Assert.Same(settings, main.SettingsNavItem.Content);
    }

    private static MainViewModel CreateMain(
        out AboutPageViewModel about,
        out SettingViewModel settings,
        out HeadlessServices.StubLoginService login)
    {
        login = new HeadlessServices.StubLoginService();
        about = new AboutPageViewModel(login, WeakReferenceMessenger.Default);
        settings = new SettingViewModel(new FakeSettingService());

        return new MainViewModel(
            NullLogger<MainViewModel>.Instance,
            login,
            new RecommendationPageViewModel(),
            about,
            settings);
    }
}
