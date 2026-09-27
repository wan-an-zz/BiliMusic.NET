using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Tests.Support;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;
using CommunityToolkit.Mvvm.Messaging;
using SukiUI.Controls;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 「关于」页：结构、头像点击入口，以及扫码弹窗能否真的渲染出来。
/// </summary>
/// <remarks>
/// 弹窗那条是重点回归：<c>SukiDialogManager</c> 只负责发事件、不做渲染，
/// 页面里少一个 <c>SukiDialogHost</c> 就会「登录服务被调用、界面却什么都不出现」。
/// </remarks>
public class AboutPageViewTests
{
    private static AboutPageViewModel NewModel(out HeadlessServices.StubLoginService login)
    {
        login = new HeadlessServices.StubLoginService
        {
            QrSession = new QrSession(
                "demo-key",
                "https://www.bilibili.com/h5/login?qrcode_key=demo",
                DateTimeOffset.Now.AddMinutes(3)),
        };

        return new AboutPageViewModel(login, WeakReferenceMessenger.Default);
    }

    private static async Task<Window> MountAsync(AboutPageView view, double width = 800, double height = 600)
        => await HeadlessViewHost.MountAsync(view, width, height);

    /// <summary>弹窗内容要塞进宿主模板，必须把布局脏标记打上再跑一轮。</summary>
    private static async Task RelayoutAsync(Window window)
    {
        window.InvalidateMeasure();
        window.InvalidateArrange();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
        await Task.Yield();
    }

    [AvaloniaFact]
    public async Task 页面被滚动视图包裹且显示默认用户名()
    {
        var model = NewModel(out _);
        var view = new AboutPageView { DataContext = model };
        var window = await MountAsync(view);

        Assert.Single(view.GetVisualDescendants().OfType<ScrollViewer>());
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == AboutPageViewModel.AppName);
    }

    [AvaloniaFact]
    public async Task 点击头像会拉起登录()
    {
        var model = NewModel(out var login);
        var view = new AboutPageView { DataContext = model };
        await MountAsync(view);

        model.OnAvatarPointerPressed();
        await Task.Yield();

        Assert.Equal(1, login.CreateSessionCallCount);
    }

    [AvaloniaFact]
    public async Task 指针悬停图标时才显示动作提示并变灰()
    {
        var model = NewModel(out _);
        var view = new AboutPageView { DataContext = model };
        var window = await MountAsync(view);

        var host = view.GetVisualDescendants().OfType<Panel>().First(p => p.Name == "AvatarHost");
        var image = view.GetVisualDescendants().OfType<Image>().First();
        var overlay = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("avatar-overlay"));

        // 未悬停时提示必须真的不显示——只做成"背景透明"会让字样一直压在图标上。
        Assert.False(overlay.IsVisible);

        // :pointerover 由输入系统设置，必须送真实指针事件，不能只 RaiseEvent。
        var center = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), window);
        Assert.NotNull(center);
        window.MouseMove(center!.Value);
        await Task.Yield();

        Assert.True(host.IsPointerOver, "指针应落在图标上");
        Assert.True(overlay.IsVisible, "悬停应显示动作提示");
        // 变淡是 0.15s 过渡，断言方向而非终值，否则会随机读到过渡中间值。
        Assert.True(image.Opacity < 1.0, $"悬停应让图标变灰，实际 Opacity={image.Opacity}");

        // 移出图标区域：提示立刻消失（IsVisible 不参与过渡）。
        window.MouseMove(new Point(1, 1));
        await Task.Yield();

        Assert.False(host.IsPointerOver, "移出后不应再处于悬停态");
        Assert.False(overlay.IsVisible, "移出后提示应消失");
    }

    [AvaloniaFact]
    public async Task 悬停提示文案随登录态切换()
    {
        var model = NewModel(out var login);
        var view = new AboutPageView { DataContext = model };
        await MountAsync(view);

        Assert.Equal(AboutPageViewModel.LoginActionText, model.AvatarActionText);

        login.RaiseLoginSucceeded(new BiliUserProfile(1, "测试用户", null, false, 6));
        await Task.Yield();

        Assert.Equal(AboutPageViewModel.LogoutActionText, model.AvatarActionText);

        model.Logout();
        await Task.Yield();

        Assert.Equal(AboutPageViewModel.LoginActionText, model.AvatarActionText);
    }

    [AvaloniaFact]
    public async Task 分割线横跨内容区()
    {
        var model = NewModel(out _);
        var view = new AboutPageView { DataContext = model };
        var window = await MountAsync(view);

        // 按名字取，别按高度猜：分割线的粗细是随时会调的外观参数。
        var divider = view.FindControl<Border>("Divider");
        Assert.NotNull(divider);

        // 比内容区略短：两端只留分割线自己的边距。
        Assert.True(
            divider!.Bounds.Width > window.Width - 60,
            $"分割线宽度 {divider.Bounds.Width}，窗口宽度 {window.Width}");
    }

    [AvaloniaFact]
    public async Task 拉起的扫码弹窗会渲染出二维码视图()
    {
        var model = NewModel(out var login);
        var view = new AboutPageView { DataContext = model };
        var window = await MountAsync(view);

        await model.BeginLogin();
        await RelayoutAsync(window);

        Assert.Equal(1, login.CreateSessionCallCount);

        // 宿主是必要的：没有它，弹窗管理器发完事件就没有任何东西去渲染对话框。
        Assert.Single(view.GetVisualDescendants().OfType<SukiDialogHost>());
        Assert.Single(window.GetVisualDescendants().OfType<QrLoginView>());
    }

    [AvaloniaFact]
    public async Task 登出确认弹窗会渲染出对话框()
    {
        var model = NewModel(out var login);
        login.IsLoggedIn = true;
        var view = new AboutPageView { DataContext = model };
        var window = await MountAsync(view);

        model.RequestLogout();
        await RelayoutAsync(window);

        Assert.Single(view.GetVisualDescendants().OfType<SukiDialogHost>());
        Assert.Contains(
            view.GetVisualDescendants().OfType<SukiUI.Controls.SukiDialog>(),
            d => d.Title == "退出登录");
    }

    [AvaloniaFact]
    public async Task 取码失败时收起弹窗()
    {
        var model = NewModel(out var login);
        login.QrSession = null;
        var view = new AboutPageView { DataContext = model };
        var window = await MountAsync(view);

        var dismissed = 0;
        model.DialogManager.OnDialogDismissed += (_, _) => dismissed++;

        await model.BeginLogin();
        await RelayoutAsync(window);

        // 宿主会为了关闭动画把内容多留 500ms，所以断言"收到了收起事件"而不是"内容已消失"。
        Assert.Equal(1, dismissed);
    }
}
