using Avalonia.Headless.XUnit;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 二维码弹窗内容：加载态判定、四类状态文案、倒计时与重新取码。
/// </summary>
public class QrLoginViewModelTests
{
    private const string QrContent = "https://www.bilibili.com/h5/login?qrcode_key=demo";

    private static QrSession NewSession(TimeSpan? lifetime = null)
        => new("demo-key", QrContent, DateTimeOffset.Now + (lifetime ?? TimeSpan.FromMinutes(3)));

    private static QrLoginViewModel NewViewModel(Action? dismiss = null)
        => new(dismiss ?? (() => { }));

    private static QrStatusResult Status(LoginState state, string message = "0")
        => new(state, 0, message);

    [AvaloniaFact]
    public void 初始处于取码中()
    {
        var vm = NewViewModel();

        Assert.True(vm.IsBusy);
        Assert.Null(vm.QrImage);
        Assert.False(vm.ShowCountdown);
    }

    [AvaloniaFact]
    public void 收到二维码后结束加载态并出图()
    {
        var vm = NewViewModel();

        vm.Receive(NewSession());

        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.QrImage);
        Assert.Equal("请使用哔哩哔哩手机客户端扫描二维码", vm.StatusText);
    }

    [AvaloniaFact]
    public void 未扫码状态不会把加载态翻回来()
    {
        // 关键回归：IsBusy 以"是否收到二维码"为准，不能用 WaitingForScan 判——
        // 取码成功那一刻轮询还没开始，拿状态判会让二维码一闪一闪。
        var vm = NewViewModel();
        vm.Receive(NewSession());

        vm.Apply(Status(LoginState.WaitingForScan));

        Assert.False(vm.IsBusy);
        Assert.NotNull(vm.QrImage);
    }

    [AvaloniaFact]
    public void 收到二维码即起倒计时()
    {
        var vm = NewViewModel();
        var session = NewSession();

        vm.Receive(session);

        Assert.True(vm.ShowCountdown);
        Assert.Equal(CountdownFormat.Remaining(session.ExpiresAt - DateTimeOffset.Now), vm.RemainingText);
    }

    [AvaloniaFact]
    public void 倒计时随时间递减()
    {
        var vm = NewViewModel();
        var session = NewSession();
        vm.Receive(session);

        vm.Tick(session.ExpiresAt - TimeSpan.FromSeconds(10));

        Assert.Equal("00:10", vm.RemainingText);
    }

    [AvaloniaFact]
    public void 倒计时归零即转入失效并给出重新取码()
    {
        var vm = NewViewModel();
        var session = NewSession();
        vm.Receive(session);

        vm.Tick(session.ExpiresAt);

        Assert.Equal("二维码失效，请重新取码", vm.StatusText);
        Assert.True(vm.ShowRefetch);
        Assert.False(vm.ShowCountdown);
        Assert.Null(vm.QrImage);
    }

    [AvaloniaFact]
    public void 倒计时归零后用新会话可以恢复()
    {
        var vm = NewViewModel();
        var session = NewSession();
        vm.Receive(session);
        vm.Tick(session.ExpiresAt);

        vm.Receive(NewSession());

        Assert.False(vm.ShowRefetch);
        Assert.NotNull(vm.QrImage);
        Assert.True(vm.ShowCountdown);
    }

    [AvaloniaFact]
    public void 待确认状态给出手机确认文案()
    {
        var vm = NewViewModel();
        vm.Receive(NewSession());

        vm.Apply(Status(LoginState.WaitingForConfirm));

        Assert.Equal("请在手机上确认登录", vm.StatusText);
        Assert.False(vm.ShowCountdown);
    }

    [AvaloniaFact]
    public void 登录成功给出成功文案并停下倒计时()
    {
        var vm = NewViewModel();
        vm.Receive(NewSession());

        vm.Apply(Status(LoginState.LoggedIn));

        Assert.Equal("登录成功", vm.StatusText);
        Assert.False(vm.ShowCountdown);
        Assert.False(vm.ShowRefetch);
    }

    [AvaloniaFact]
    public void 失效状态给出重新取码()
    {
        var vm = NewViewModel();
        vm.Receive(NewSession());

        vm.Apply(Status(LoginState.Expired));

        Assert.Equal("二维码失效，请重新取码", vm.StatusText);
        Assert.True(vm.ShowRefetch);
    }

    [AvaloniaFact]
    public void 超时状态给出重新取码()
    {
        var vm = NewViewModel();
        vm.Receive(NewSession());

        vm.Apply(Status(LoginState.TimedOut));

        Assert.Equal("二维码失效，请重新取码", vm.StatusText);
        Assert.True(vm.ShowRefetch);
    }

    [AvaloniaFact]
    public void 失败状态给出服务器问题文案且不给重新取码()
    {
        var vm = NewViewModel();
        vm.Receive(NewSession());

        vm.Apply(Status(LoginState.Failed));

        Assert.Equal("连接服务器时出现问题", vm.StatusText);
        Assert.False(vm.ShowRefetch);
    }

    [AvaloniaFact]
    public void 轮询终局映射到相同文案()
    {
        var succeeded = NewViewModel();
        succeeded.Receive(NewSession());
        succeeded.ApplyOutcome(QrLoginOutcome.Succeeded);
        Assert.Equal("登录成功", succeeded.StatusText);

        var failed = NewViewModel();
        failed.Receive(NewSession());
        failed.ApplyOutcome(QrLoginOutcome.NetworkFailed);
        Assert.Equal("连接服务器时出现问题", failed.StatusText);
    }

    [AvaloniaFact]
    public void 取消后不再响应任何状态推进()
    {
        var vm = NewViewModel();
        vm.Receive(NewSession());

        vm.Cancel();
        vm.Apply(Status(LoginState.WaitingForConfirm));
        vm.Tick(DateTimeOffset.Now);

        Assert.NotEqual("请在手机上确认登录", vm.StatusText);
        Assert.False(vm.ShowCountdown);
    }

    [AvaloniaFact]
    public void 取消会中断轮询令牌()
    {
        var vm = NewViewModel();
        vm.Receive(NewSession());
        var token = vm.Token;

        vm.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    [AvaloniaFact]
    public void 重新取码先收弹窗再通知关于页()
    {
        var dismissed = false;
        var received = 0;
        var recipient = new object();

        // 视图模型走的是默认 messenger，订阅点也必须挂在它上面才能收到。
        WeakReferenceMessenger.Default.Register<ReLoginRequestMessage>(recipient, (_, _) => received++);
        try
        {
            var vm = new QrLoginViewModel(() => dismissed = true);

            vm.RefetchCommand.Execute(null);

            Assert.True(dismissed);
            Assert.Equal(1, received);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }
}
