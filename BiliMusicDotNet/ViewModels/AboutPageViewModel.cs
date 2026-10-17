using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.ViewModels.Messages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SukiUI.Dialogs;
using CherylUI.Dialogs;

namespace BiliMusicDotNet.ViewModels;

/// <summary>
/// 「关于」页：展示当前登录者的头像与用户名，并承载登录 / 登出两个入口。
/// </summary>
public sealed partial class AboutPageViewModel : ViewModelBase
{
    /// <summary>未登录时显示的应用名，同时也是登录态回退值。</summary>
    public const string AppName = "BiliMusic.NET";

    /// <summary>应用图标占位图在程序集里的位置。</summary>
    private static readonly Uri PlaceholderUri = new("avares://BiliMusicDotNet/Assets/图标占位图.png");

    private readonly IBiliLoginService _loginService;
    private readonly IMessenger _messenger;
    private readonly ILogger<AboutPageViewModel> _logger;

    private bool _isLoginSucceededSubscribed;

    /// <summary>
    /// 由容器创建。
    /// </summary>
    /// <param name="loginService">登录服务：取码、轮询、恢复登录态、登出。</param>
    /// <param name="messenger">视图模型之间的消息通道。</param>
    /// <param name="logger">日志器。</param>
    public AboutPageViewModel(
        IBiliLoginService loginService,
        IMessenger messenger,
        ILogger<AboutPageViewModel>? logger = null)
    {
        _loginService = loginService;
        _messenger = messenger;
        _logger = logger ?? NullLogger<AboutPageViewModel>.Instance;

        HeadPhoto = LoadPlaceholder();
        SubscribeLoginSucceeded();

        // 换码要先把当前弹窗收掉再开新的（弹窗管理器同时只允许一个），所以这条消息落在这里。
        _messenger.Register<ReLoginRequestMessage>(this, (_, _) => _ = BeginLogin());
    }

    /// <summary>页面上的弹窗管理器；视图里的 <c>SukiDialogHost</c> 绑它。仅用于Windows平台</summary>
    public ISukiDialogManager DialogManagerWindows { get; } = new SukiDialogManager();

    /// <summary>页面上的弹窗管理器；视图里的 <c>SukiDialogHost</c> 绑它。仅用于 Android平台</summary>
    public ICherylDialogManager DialogManagerAndroid { get; } = new CherylDialogManager();

    /// <summary>圆形图标显示的内容：未登录是应用图标，已登录是头像。</summary>
    [ObservableProperty]
    private Bitmap? _headPhoto;

    /// <summary>图标下方的文本：未登录是应用名，已登录是用户名。</summary>
    [ObservableProperty]
    private string _userName = AppName;

    /// <summary>指针悬停在图标上时显示的动作提示文案。</summary>
    [ObservableProperty]
    private string _avatarActionText = LoginActionText;

    /// <summary>未登录时悬停提示的操作。</summary>
    public const string LoginActionText = "立即登录";

    /// <summary>已登录时悬停提示的操作。</summary>
    public const string LogoutActionText = "退出登录";

    /// <summary>头像被点击：未登录去登录，已登录弹二次确认后登出。</summary>
    public void OnAvatarPointerPressed()
    {
        if (_loginService.IsLoggedIn)
        {
            RequestLogout();
        }
        else
        {
            // 点击处理器不能 await；BeginLogin 自己吞掉全部异常，抛不出来。
            _ = BeginLogin();
        }
    }

    /// <summary>
    /// 拉起扫码登录：拉起弹窗、取码、把码交给二维码那侧的视图模型，然后等扫码结果。
    /// </summary>
    /// <returns>整个扫码流程结束（含用户关闭弹窗、取码失败）后完成的异步操作。</returns>
    public async Task BeginLogin()
    {
        // 上一轮登录成功时退过订，这里重新挂上，否则这次登录成功后界面不会更新。
        SubscribeLoginSucceeded();

        var qrViewModel = new QrLoginViewModel(DismissDialog, _messenger);

#if WINDOWS
        if (!DialogManagerWindows.CreateDialog()
                .WithTitle("扫码登录")
                .WithContent(qrViewModel)
                .Dismiss().ByClickingBackground()
                .OnDismissed(_ => qrViewModel.Cancel())
                .TryShow())
        {
            _logger.LogWarning("已有弹窗在显示，本次登录未开始");
            qrViewModel.Cancel();
            return;
        }
#endif

#if ANDROID
        if (!DialogManagerAndroid.CreateDialog()
                .WithTitle("扫码登录")
                .WithContent(qrViewModel)
                .Dismiss().ByClickingBackground()
                .OnDismissed(_ => qrViewModel.Cancel())
                .TryShow())
        {
            _logger.LogWarning("已有弹窗在显示，本次登录未开始");
            qrViewModel.Cancel();
            return;
        }
#endif
        
        try
        {
            var session = await _loginService.CreateQrSessionAsync(qrViewModel.Token).ConfigureAwait(true);
            qrViewModel.Receive(session);

            var progress = new Progress<QrStatusResult>(status => qrViewModel.Apply(status));
            var result = await _loginService
                .WaitForConfirmationAsync(session, progress, qrViewModel.Token)
                .ConfigureAwait(true);

            qrViewModel.ApplyOutcome(result.Outcome);
        }
        catch (OperationCanceledException)
        {
            // 用户关掉了弹窗：取消是正常出口，不是错误。
            _logger.LogInformation("扫码登录已取消");
        }
        catch (Exception ex) when (ex is BiliApiException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "取码或轮询失败");
            DismissDialog();
            qrViewModel.Cancel();
        }
    }

    /// <summary>
    /// 请求登出：先弹二次确认，确认后才真正退出。
    /// </summary>
    public void RequestLogout()
    {
#if WINDOWS
        if (!DialogManagerWindows.CreateDialog()
                .WithTitle("退出登录")
                .WithContent($"确定要退出「{UserName}」吗？")
                .WithActionButton("取消", _ => DialogManagerWindows.DismissDialog(), false, "Flat")
                .WithActionButton(
                    "退出登录",
                    _ =>
                    {
                        DialogManagerWindows.DismissDialog();
                        Logout();
                    },
                    dismissOnClick: false,
                    classes: ["Accent"])
                .TryShow())
        {
            _logger.LogWarning("已有弹窗在显示，本次登出未开始");
        }
#endif
        
#if ANDROID
        if (!DialogManagerAndroid.CreateDialog()
                .WithTitle("退出登录")
                .WithContent($"确定要退出「{UserName}」吗？")
                .WithActionButton("取消", _ => DialogManagerAndroid.DismissDialog(), false, "Text")
                .WithActionButton(
                    "退出登录",
                    _ =>
                    {
                        DialogManagerAndroid.DismissDialog();
                        Logout();
                    },
                    dismissOnClick: false,
                    classes: ["Secondary"])
                .TryShow())
        {
            _logger.LogWarning("已有弹窗在显示，本次登出未开始");
        }
#endif
        
    }

    /// <summary>
    /// 执行登出并把界面恢复到未登录的样子。
    /// </summary>
    public void Logout()
    {
        // 先订阅再调服务：登出是同步完成的，晚了就收不到事件。
        void OnLoggedOut(object? sender, LoggedOutEventArgs e) => RestoreDefaults();

        _loginService.LoggedOut += OnLoggedOut;
        try
        {
            _loginService.Logout();
        }
        catch (StorageException ex)
        {
            // 磁盘副本删不掉，但内存与 Cookie 已经清干净，界面仍按已登出渲染。
            _logger.LogError(ex, "登出时未能清理本地凭据副本");
        }
        finally
        {
            _loginService.LoggedOut -= OnLoggedOut;

            // 不能只依赖 LoggedOut：删盘失败时服务会先抛异常，事件永远不来，
            // 界面就会停在"已登录"。恢复默认值放在这里，两条出口都会走到。
            RestoreDefaults();
        }
    }

    /// <summary>订阅登录成功，等它触发时换头像与用户名；换完退订。</summary>
    private void SubscribeLoginSucceeded()
    {
        if (_isLoginSucceededSubscribed)
        {
            return;
        }

        _loginService.LoginSucceeded += OnLoginSucceeded;
        _isLoginSucceededSubscribed = true;
    }

    private void UnsubscribeLoginSucceeded()
    {
        if (!_isLoginSucceededSubscribed)
        {
            return;
        }

        _loginService.LoginSucceeded -= OnLoginSucceeded;
        _isLoginSucceededSubscribed = false;
    }

    private void OnLoginSucceeded(object? sender, LoginSucceededEventArgs e)
    {
        UnsubscribeLoginSucceeded();

        // 事件来自服务内部的续体（线程池线程），而这里要碰位图与绑定属性。
        if (Dispatcher.UIThread.CheckAccess())
        {
            _ = ApplyProfileAsync(e.Profile);
            return;
        }

        Dispatcher.UIThread.Post(() => _ = ApplyProfileAsync(e.Profile));
    }

    private async Task ApplyProfileAsync(BiliUserProfile? profile)
    {
        UserName = string.IsNullOrWhiteSpace(profile?.Nickname) ? AppName : profile!.Nickname;
        AvatarActionText = LogoutActionText;

        if (profile?.AvatarUri is not { } avatarUri)
        {
            return;
        }

        try
        {
            var bytes = await _loginService.GetAvatarImageAsync(avatarUri).ConfigureAwait(true);
            using var stream = new MemoryStream(bytes ?? []);

            // 取不到或解不开就保留占位图：头像只是展示要素，不该把已登录的状态弄丢。
            HeadPhoto = bytes is { Length: > 0 } ? new Bitmap(stream) : HeadPhoto;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "头像解码失败，继续用占位图");
        }
    }

    /// <summary>把界面恢复成未登录时的默认值。</summary>
    private void RestoreDefaults()
    {
        UserName = AppName;
        AvatarActionText = LoginActionText;
        HeadPhoto = LoadPlaceholder();
    }

    private void DismissDialog()
    {
#if WINDOWS
        DialogManagerWindows.DismissDialog();
#endif
        
#if ANDROID
        DialogManagerAndroid.DismissDialog();
#endif
    } 

    private Bitmap? LoadPlaceholder()
    {
        try
        {
            using var stream = AssetLoader.Open(PlaceholderUri);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or NotSupportedException)
        {
            // 占位图缺失不该让「关于」页崩掉：界面退化成"没有图"而已。
            _logger.LogWarning(ex, "占位图加载失败：{Uri}", PlaceholderUri);
            return null;
        }
    }
}
