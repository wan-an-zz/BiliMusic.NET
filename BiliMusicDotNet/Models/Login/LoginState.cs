namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 登录状态机（UI 渲染依据）。
/// </summary>
/// <remarks>
/// 终态判定见 <c>IBiliLoginService.IsTerminal</c>：到达终态后无需再轮询。
/// </remarks>
public enum LoginState
{
    /// <summary>未登录且无进行中的扫码流程。</summary>
    Idle,

    /// <summary>已取到二维码，等待用户扫码。</summary>
    WaitingForScan,

    /// <summary>用户已扫码，等待其在手机上点击确认。</summary>
    WaitingForConfirm,

    /// <summary>登录成功，凭据已就绪。</summary>
    LoggedIn,

    /// <summary>二维码失效，需重新取码。</summary>
    Expired,

    /// <summary>等待超时，需重新取码。</summary>
    TimedOut,

    /// <summary>网络连续失败导致流程中断。</summary>
    Failed,
}
