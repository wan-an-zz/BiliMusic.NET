using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BiliMusicDotNet.Models.Settings;
using BiliMusicDotNet.Tests.Support;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;
using SukiUI.Controls;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 设置页桌面分支：分区卡片、两种设置控件、空态与返回。
/// </summary>
/// <remarks>
/// 测试项目只跑 Windows Headless，因此覆盖 <c>OnPlatform</c> 的桌面分支；
/// Android 的 SliverPage 分支靠 Android 构建与实机验收。
/// </remarks>
public sealed class SettingViewTests
{
    [AvaloniaFact]
    public async Task 桌面分支渲染SukiGroupBox()
    {
        var item = Toggle();
        var (_, view) = await MountAsync(new FakeSettingService(Group(item)));

        Assert.Single(view.GetVisualDescendants().OfType<SukiUI.Controls.GroupBox>());
    }

    [AvaloniaFact]
    public async Task Toggle项渲染ToggleSwitch()
    {
        var item = Toggle();
        var (_, view) = await MountAsync(new FakeSettingService(Group(item)));

        Assert.Single(view.GetVisualDescendants().OfType<ToggleSwitch>());
    }

    [AvaloniaFact]
    public async Task SingleChoice项渲染ComboBox()
    {
        var item = Choice();
        var (_, view) = await MountAsync(new FakeSettingService(Group(item)));

        Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
    }

    [AvaloniaFact]
    public async Task ComboBox展示选项标题()
    {
        var item = Choice();
        var (_, view) = await MountAsync(new FakeSettingService(Group(item)));

        var comboBox = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());

        Assert.Equal(new[] { "浅色", "深色" }, comboBox.ItemsSource!.Cast<string>().ToArray());
    }

    [AvaloniaFact]
    public async Task 没有描述时不显示描述文本()
    {
        var item = Toggle(description: null);
        var (_, view) = await MountAsync(new FakeSettingService(Group(item)));

        var title = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == item.Title);
        var stack = Assert.IsType<StackPanel>(title.Parent);
        var description = Assert.IsType<TextBlock>(stack.Children[1]);

        Assert.False(description.IsVisible);
    }

    [AvaloniaFact]
    public async Task 空态显示提示文案()
    {
        var (_, view) = await MountAsync(new FakeSettingService());

        var empty = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "暂无可配置的设置项");

        Assert.True(empty.IsVisible);
    }

    [AvaloniaFact]
    public async Task 点击返回触发CloseRequested()
    {
        var viewModel = new SettingViewModel(new FakeSettingService());
        var (_, view) = await MountAsync(viewModel);
        var back = view.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "DesktopBackButton");
        var raised = 0;
        viewModel.CloseRequested += (_, _) => raised++;

        back.Command!.Execute(null);

        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public async Task 切换Toggle写回Value并调用服务()
    {
        var item = Toggle();
        var service = new FakeSettingService(Group(item));
        var (_, view) = await MountAsync(service);
        var toggle = Assert.Single(view.GetVisualDescendants().OfType<ToggleSwitch>());

        toggle.IsChecked = false;
        await Task.Yield();

        Assert.False(Assert.IsType<bool>(item.Value));
        Assert.Contains(item, service.ChangedItems);
    }

    [AvaloniaFact]
    public async Task 选择下拉项写回Value()
    {
        var item = Choice();
        var service = new FakeSettingService(Group(item));
        var (_, view) = await MountAsync(service);
        var comboBox = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());

        comboBox.SelectedItem = "深色";
        await Task.Yield();

        Assert.Equal("dark", item.Value);
        Assert.Contains(item, service.ChangedItems);
    }

    private static async Task<(Window Window, SettingView View)> MountAsync(SettingViewModel viewModel)
        => await MountAsync(viewModel, 800, 600);

    private static async Task<(Window Window, SettingView View)> MountAsync(
        FakeSettingService service,
        double width = 800,
        double height = 600)
    {
        var viewModel = new SettingViewModel(service);
        var view = new SettingView { DataContext = viewModel };
        var window = await HeadlessViewHost.MountAsync(view, width, height);
        return (window, view);
    }

    private static async Task<(Window Window, SettingView View)> MountAsync(
        SettingViewModel viewModel,
        double width,
        double height)
    {
        var view = new SettingView { DataContext = viewModel };
        var window = await HeadlessViewHost.MountAsync(view, width, height);
        return (window, view);
    }

    private static SettingSectionGroup Group(params SettingItem[] items)
        => new(SettingSection.About, "关于设置", 1, items);

    private static SettingItem Toggle(string name = "demo", bool value = true, string? description = null)
    {
        return new SettingItem
        {
            Section = SettingSection.About,
            Method = SettingMethod.Toggle,
            Type = typeof(SettingViewTests),
            Name = name,
            Title = "演示开关",
            Description = description,
            Value = value,
        };
    }

    private static SettingItem Choice(string name = "demo", string value = "light")
    {
        return new SettingItem
        {
            Section = SettingSection.About,
            Method = SettingMethod.SingleChoice,
            Type = typeof(SettingViewTests),
            Name = name,
            Title = "演示选择",
            Options =
            [
                new SettingOption("light", "浅色"),
                new SettingOption("dark", "深色"),
            ],
            Value = value,
        };
    }
}
