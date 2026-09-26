using System;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BiliMusicDotNet.Models.Login;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QRCoder;

namespace BiliMusicDotNet.ViewModels;

/// <summary>
/// 扫码弹窗里的那块内容：二维码位图、状态文案与剩余有效期倒计时。
/// </summary>
/// <remarks>
/// 状态推进有两条来源：会话（<see cref="Receive"/>，决定"什么时候有码、什么时候起倒计时"）
/// 与登录服务的状态查询（<see cref="Apply"/>，决定文案）。两者分开，是因为取码成功那一刻
/// 轮询还没开始，不能拿 <see cref="LoginState.WaitingForScan"/> 当"码已就绪"用。
/// </remarks>
public sealed partial class QrLoginViewModel : ViewModelBase
{
    /// <summary>倒计时刷新间隔。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly Action _dismissDialog;
    private readonly IMessenger _messenger;
    private readonly ILogger<QrLoginViewModel> _logger;

    private DispatcherTimer? _countdownTimer;
    private QrSession? _session;
    private CancellationTokenSource? _cancellation;
    private bool _isCancelled;

    /// <summary>
    /// 由容器创建。
    /// </summary>
    /// <param name="dismissDialog">
    /// 请求收起宿主弹窗。二维码不自己重新取码：换码要先把当前弹窗收掉再开新的，
    /// 而弹窗管理器同一时刻只允许一个。
    /// </param>
    /// <param name="messenger">消息通道；必须与订阅方是同一个实例，否则换码消息发不到。</param>
    /// <param name="logger">日志器。</param>
    public QrLoginViewModel(
        Action dismissDialog,
        IMessenger? messenger = null,
        ILogger<QrLoginViewModel>? logger = null)
    {
        _dismissDialog = dismissDialog ?? throw new ArgumentNullException(nameof(dismissDialog));
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _logger = logger ?? NullLogger<QrLoginViewModel>.Instance;
    }

    /// <summary>是否还在取码（<c>true</c> 时界面显示加载动画并压暗内容）。</summary>
    [ObservableProperty]
    private bool _isBusy = true;

    /// <summary>二维码位图；取码失败或已失效时为空。</summary>
    [ObservableProperty]
    private Bitmap? _qrImage;

    /// <summary>面向用户的说明文本。</summary>
    [ObservableProperty]
    private string _statusText = "正在获取二维码…";

    /// <summary>是否显示倒计时。</summary>
    [ObservableProperty]
    private bool _showCountdown;

    /// <summary>剩余有效期文本，形如 <c>02:37</c>。</summary>
    [ObservableProperty]
    private string _remainingText = string.Empty;

    /// <summary>是否显示「重新取码」按钮。</summary>
    [ObservableProperty]
    private bool _showRefetch;

    /// <summary>本次会话的取消令牌，供轮询使用；未开始或已取消时抛出。</summary>
    public CancellationToken Token => (_cancellation ??= new CancellationTokenSource()).Token;
    
    /// <summary>用户点击「重新取码」：请宿主收起弹窗，由「关于」页重走一次登录流程。</summary>
    [RelayCommand]
    private void Refetch()
    {
        _dismissDialog();
        _messenger.Send(new Messages.ReLoginRequestMessage());
    }

    /// <summary>
    /// 收到二维码会话：出图、停掉加载态、起倒计时。
    /// </summary>
    /// <param name="session">登录服务返回的会话。</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> 为 <see langword="null"/>。</exception>
    public void Receive(QrSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _isCancelled = false;
        ShowRefetch = false;

        QrImage = RenderQrCode(session.Content);
        IsBusy = false;
        StatusText = "请使用哔哩哔哩手机客户端扫描二维码";

        Tick(DateTimeOffset.Now);
        StartCountdown();
    }

    /// <summary>
    /// 推进一次倒计时。计时器每秒调它；测试可直接喂时间点，无需等真实时钟。
    /// </summary>
    /// <param name="now">当前时间。</param>
    public void Tick(DateTimeOffset now)
    {
        if (_session is not { } session || _isCancelled)
        {
            return;
        }

        var remaining = session.ExpiresAt - now;
        if (CountdownFormat.HasExpired(remaining))
        {
            ShowExpired();
            return;
        }

        RemainingText = CountdownFormat.Remaining(remaining);
        ShowCountdown = true;
    }

    /// <summary>
    /// 应用一次状态查询结果，据此改文案。
    /// </summary>
    /// <param name="status">状态查询结果。</param>
    /// <exception cref="ArgumentNullException"><paramref name="status"/> 为 <see langword="null"/>。</exception>
    public void Apply(QrStatusResult status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (_isCancelled)
        {
            return;
        }

        switch (status.State)
        {
            case LoginState.WaitingForConfirm:
                StopCountdown();
                StatusText = "请在手机上确认登录";
                break;

            case LoginState.LoggedIn:
                StopCountdown();
                StatusText = "登录成功";
                break;

            case LoginState.Expired:
            case LoginState.TimedOut:
                ShowExpired();
                break;

            case LoginState.Failed:
                StopCountdown();
                StatusText = "连接服务器时出现问题";
                break;
        }
    }

    /// <summary>
    /// 轮询终局：成功与各类失败各给一条终态文案。
    /// </summary>
    /// <param name="outcome">终局类型。</param>
    public void ApplyOutcome(QrLoginOutcome outcome)
    {
        switch (outcome)
        {
            case QrLoginOutcome.Succeeded:
                Apply(new QrStatusResult(LoginState.LoggedIn, 0, "登录成功", null));
                break;

            case QrLoginOutcome.Expired:
            case QrLoginOutcome.TimedOut:
                ShowExpired();
                break;

            default:
                Apply(new QrStatusResult(LoginState.Failed, -1, "连接服务器时出现问题", null));
                break;
        }
    }

    /// <summary>
    /// 取消本次会话：停倒计时并取消轮询，之后任何状态推进都不再改界面。
    /// </summary>
    public void Cancel()
    {
        _isCancelled = true;
        StopCountdown();

        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    /// <summary>把界面推进到"二维码已失效，可重新取码"。</summary>
    private void ShowExpired()
    {
        StopCountdown();
        QrImage = null;
        ShowCountdown = false;
        StatusText = "二维码失效，请重新取码";
        ShowRefetch = true;
    }

    private void StartCountdown()
    {
        // 刚出完码就可能已经过期（Tick 已经把界面推进到"重新取码"）：此时不该再起计时器。
        // 注意不要在这里调 StopCountdown()，它会把 Tick 刚点亮的倒计时又熄掉。
        if (ShowRefetch)
        {
            return;
        }

        _countdownTimer = new DispatcherTimer { Interval = TickInterval };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
    }

    private void StopCountdown()
    {
        ShowCountdown = false;

        if (_countdownTimer is not { } timer)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnCountdownTick;
        _countdownTimer = null;
    }

    private void OnCountdownTick(object? sender, EventArgs e) => Tick(DateTimeOffset.Now);

    /// <summary>
    /// 用 QRCoder 把内容渲染成位图。
    /// </summary>
    /// <param name="content">二维码内容。</param>
    /// <returns>位图；渲染失败时返回 <see langword="null"/>，由调用方把界面引到"重新取码"。</returns>
    private Bitmap? RenderQrCode(string content)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
            using var renderer = new PngByteQRCode(data);

            using var stream = new System.IO.MemoryStream(renderer.GetGraphic(10, drawQuietZones: true));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            _logger.LogError(ex, "二维码渲染失败，内容不可用");
            return null;
        }
    }
}
