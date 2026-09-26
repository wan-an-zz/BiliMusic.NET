using Avalonia.Headless.XUnit;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Tests.Support;
using BiliMusicDotNet.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 「关于」页视图模型：默认值、登录成功后换头像换名、登出恢复默认，以及取码失败的收尾。
/// </summary>
public class AboutPageViewModelTests
{
    private static AboutPageViewModel NewViewModel(HeadlessServices.StubLoginService login)
        => new(login, WeakReferenceMessenger.Default);

    private static BiliUserProfile Profile(string nickname, string? avatarUrl)
        => new(123456, nickname, avatarUrl is null ? null : new Uri(avatarUrl), IsVip: false, Level: 6);

    private static QrSession Session()
        => new("demo-key", "https://www.bilibili.com/h5/login?qrcode_key=demo", DateTimeOffset.Now.AddMinutes(3));

    private static AboutPageViewModel LoggedInViewModel(out HeadlessServices.StubLoginService login, bool throwOnLogout = false)
    {
        login = new HeadlessServices.StubLoginService
        {
            AvatarBytes = TestImages.Png,
            IsLoggedIn = true,
            ThrowOnLogout = throwOnLogout,
        };

        var vm = NewViewModel(login);
        login.RaiseLoginSucceeded(Profile("测试用户", "https://i0.hdslb.com/bfs/face/demo.jpg"));
        Assert.Equal("测试用户", vm.UserName);

        return vm;
    }

    [AvaloniaFact]
    public void 初始显示应用名与占位图()
    {
        var vm = NewViewModel(new HeadlessServices.StubLoginService());

        Assert.Equal(AboutPageViewModel.AppName, vm.UserName);
        Assert.NotNull(vm.HeadPhoto);
    }

    [AvaloniaFact]
    public void 登录成功后换成昵称与头像()
    {
        var vm = LoggedInViewModel(out _);

        Assert.Equal("测试用户", vm.UserName);
        Assert.NotNull(vm.HeadPhoto);
    }

    [AvaloniaFact]
    public void 资料接口失败时昵称回退为应用名()
    {
        // Profile 为空是正常路径：凭据已经到手（登录成立），只是资料没抓到。
        var login = new HeadlessServices.StubLoginService();
        var vm = NewViewModel(login);

        login.RaiseLoginSucceeded(null);

        Assert.Equal(AboutPageViewModel.AppName, vm.UserName);
        Assert.NotNull(vm.HeadPhoto);
    }

    [AvaloniaFact]
    public void 头像取不到时保留占位图()
    {
        var login = new HeadlessServices.StubLoginService { AvatarBytes = null };
        var vm = NewViewModel(login);
        var placeholder = vm.HeadPhoto;

        login.RaiseLoginSucceeded(Profile("测试用户", "https://i0.hdslb.com/bfs/face/demo.jpg"));

        Assert.Equal("测试用户", vm.UserName);
        Assert.Same(placeholder, vm.HeadPhoto);
    }

    [AvaloniaFact]
    public void 头像字节不是图片时保留占位图且不抛异常()
    {
        var login = new HeadlessServices.StubLoginService { AvatarBytes = [1, 2, 3] };
        var vm = NewViewModel(login);
        var placeholder = vm.HeadPhoto;

        login.RaiseLoginSucceeded(Profile("测试用户", "https://i0.hdslb.com/bfs/face/demo.jpg"));

        Assert.Equal("测试用户", vm.UserName);
        Assert.Same(placeholder, vm.HeadPhoto);
    }

    [AvaloniaFact]
    public void 登出后恢复默认()
    {
        var vm = LoggedInViewModel(out var login);

        vm.Logout();

        Assert.Equal(AboutPageViewModel.AppName, vm.UserName);
        Assert.NotNull(vm.HeadPhoto);
        Assert.False(login.IsLoggedIn);
    }

    [AvaloniaFact]
    public void 删盘失败时仍按已登出渲染()
    {
        var vm = LoggedInViewModel(out _, throwOnLogout: true);

        vm.Logout();

        // 内存与 Cookie 已被服务清干净，界面必须跟着走，不能停在"已登录"。
        Assert.Equal(AboutPageViewModel.AppName, vm.UserName);
    }

    [AvaloniaFact]
    public void 未登录时点头像走登录而不是登出()
    {
        var login = new HeadlessServices.StubLoginService { IsLoggedIn = false };
        var vm = NewViewModel(login);
        string? title = null;
        vm.DialogManager.OnDialogShown += (_, e) => title = e.Dialog.Title;

        vm.OnAvatarPointerPressed();

        // 登出路径弹的是"退出登录"确认框；这里必须是登录那条。
        Assert.Equal("扫码登录", title);
    }

    [AvaloniaFact]
    public async Task 取码失败时收起弹窗()
    {
        var login = new HeadlessServices.StubLoginService { QrSession = null };
        var vm = NewViewModel(login);
        var shown = 0;
        var dismissed = 0;
        vm.DialogManager.OnDialogShown += (_, _) => shown++;
        vm.DialogManager.OnDialogDismissed += (_, _) => dismissed++;

        await vm.BeginLogin();

        Assert.Equal(1, login.CreateSessionCallCount);
        Assert.Equal(1, shown);
        // 取码失败要顺手把弹窗收掉，否则界面上留着一个空的加载框。
        Assert.Equal(1, dismissed);
    }

    [AvaloniaFact]
    public async Task 取码成功后弹出扫码弹窗并等待结果()
    {
        var login = new HeadlessServices.StubLoginService
        {
            QrSession = Session(),
            QrResult = new QrLoginResult(QrLoginOutcome.Expired, null, null, "二维码已失效"),
        };
        var vm = NewViewModel(login);
        var shown = 0;
        var dismissed = 0;
        vm.DialogManager.OnDialogShown += (_, _) => shown++;
        vm.DialogManager.OnDialogDismissed += (_, _) => dismissed++;

        await vm.BeginLogin();

        Assert.Equal(1, login.CreateSessionCallCount);
        Assert.Equal(1, shown);
        // 轮询终局是"失效"：弹窗留着让用户看到"重新取码"，所以还不能收。
        Assert.Equal(0, dismissed);
    }
}
