using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Tests.Support;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 扫码弹窗视图：加载态、二维码位图、倒计时、四类文案与「重新取码」按钮的绑定。
/// </summary>
/// <remarks>
/// 断言走的是"视图属性随视图模型变化"，因此既覆盖 XAML 绑定也覆盖视图模型的状态推进。
/// </remarks>
public class QrLoginViewTests
{
    private const string QrContent = "https://www.bilibili.com/h5/login?qrcode_key=demo";

    private static QrSession Session(TimeSpan? lifetime = null)
        => new("demo-key", QrContent, DateTimeOffset.Now + (lifetime ?? TimeSpan.FromMinutes(3)));

    private static QrStatusResult Status(LoginState state, string message = "0")
        => new(state, 0, message);

    /// <summary>把视图挂进窗口并跑一次布局：绑定要有可视树才会生效。</summary>
    private static async Task<(Window Window, QrLoginView View, QrLoginViewModel Model)> MountAsync(Action? dismiss = null)
    {
        var model = new QrLoginViewModel(dismiss ?? (() => { }));
        var view = new QrLoginView { DataContext = model };
        var window = await HeadlessViewHost.MountAsync(view);

        return (window, view, model);
    }

    private static T Descendant<T>(Visual root) where T : Visual
        => root.GetVisualDescendants().OfType<T>().First();

    private static TextBlock CountdownText(Visual root)
        => root.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text?.StartsWith("二维码剩余", StringComparison.Ordinal) == true);

    private static IEnumerable<TextBlock> Texts(Visual root)
        => root.GetVisualDescendants().OfType<TextBlock>();

    [AvaloniaFact]
    public void 视图模型能被视图定位器解析成扫码视图()
    {
        var locator = new ViewLocator(App.Services!);
        var model = new QrLoginViewModel(() => { });

        var view = locator.Build(model);

        // 弹窗内容交给 ViewLocator 解析，这条链断了对话框里就是一片空白。
        var qrView = Assert.IsType<QrLoginView>(view);
        Assert.Same(model, qrView.DataContext);
    }

    [AvaloniaFact]
    public void 扫码视图已注册到容器()
    {
        // ViewLocator 优先从容器取视图；漏注册会静默退化成无参构造（这里能过但要保持一致）。
        Assert.NotNull(App.Services!.GetService(typeof(QrLoginView)));
    }

    [AvaloniaFact]
    public async Task 取码期间处于加载态()
    {
        var (_, view, _) = await MountAsync();

        Assert.True(Descendant<BusyArea>(view).IsBusy);
    }

    [AvaloniaFact]
    public async Task 收到二维码后结束加载态并显示位图()
    {
        var (_, view, model) = await MountAsync();

        model.Receive(Session());
        await Task.Yield();

        Assert.False(Descendant<BusyArea>(view).IsBusy);
        Assert.NotNull(Descendant<Image>(view).Source);
    }

    [AvaloniaFact]
    public async Task 倒计时文本随剩余时间变化()
    {
        var (_, view, model) = await MountAsync();
        var session = Session();

        model.Receive(session);
        model.Tick(session.ExpiresAt - TimeSpan.FromSeconds(37));
        await Task.Yield();

        var countdown = CountdownText(view);
        Assert.True(countdown.IsVisible);
        Assert.Equal("二维码剩余 00:37", countdown.Text);
    }

    [AvaloniaFact]
    public async Task 失效后隐藏倒计时并给出重新取码按钮()
    {
        var (_, view, model) = await MountAsync();
        model.Receive(Session());

        model.Apply(Status(LoginState.Expired, "二维码已失效"));
        await Task.Yield();

        Assert.False(CountdownText(view).IsVisible);
        Assert.Contains(Texts(view), t => t.Text == "二维码失效，请重新取码");
        Assert.True(view.FindControl<Button>("RefetchButton")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task 失效前不显示重新取码按钮()
    {
        var (_, view, model) = await MountAsync();

        model.Receive(Session());
        await Task.Yield();

        Assert.False(view.FindControl<Button>("RefetchButton")!.IsVisible);
    }

    [AvaloniaFact]
    public async Task 各状态显示对应文案()
    {
        var (_, view, model) = await MountAsync();
        model.Receive(Session());

        model.Apply(Status(LoginState.WaitingForConfirm));
        await Task.Yield();
        Assert.Contains(Texts(view), t => t.Text == "请在手机上确认登录");

        model.Apply(Status(LoginState.LoggedIn));
        await Task.Yield();
        Assert.Contains(Texts(view), t => t.Text == "登录成功");

        model.Apply(Status(LoginState.Failed));
        await Task.Yield();
        Assert.Contains(Texts(view), t => t.Text == "连接服务器时出现问题");
    }

    [AvaloniaFact]
    public async Task 点击重新取码会收起弹窗()
    {
        var dismissed = false;
        var (_, view, model) = await MountAsync(() => dismissed = true);
        model.Receive(Session());
        model.Apply(Status(LoginState.Expired, "失效"));
        await Task.Yield();

        view.FindControl<Button>("RefetchButton")!.Command?.Execute(null);

        Assert.True(dismissed);
    }
}
