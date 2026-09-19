namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 轮询可能的终局类型。
/// </summary>
public enum QrLoginOutcome
{
    /// <summary>登录成功，已拿到 Cookie。</summary>
    Succeeded,

    /// <summary>二维码已失效，必须重新调用 <c>IBiliLoginService.CreateQrSessionAsync</c> 取新码。</summary>
    Expired,

    /// <summary>超过二维码有效期（约 3 分钟）仍未完成扫码，本地兜底退出。</summary>
    TimedOut,

    /// <summary>连续网络失败达到上限，会话可能仍有效，可提示用户后重试。</summary>
    NetworkFailed,
}
