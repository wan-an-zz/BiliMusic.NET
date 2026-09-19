namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 登录成功后可用的凭据（与写操作所需的 CSRF 令牌）。
/// </summary>
/// <param name="SessData">登录凭据，等价于账号登录态，<b>必须加密存储</b>。</param>
/// <param name="BiliJct">CSRF 令牌，所有写操作（点赞/收藏等）需作为 <c>csrf</c> 参数提交。</param>
/// <param name="DedeUserId">当前用户 <c>mid</c>。</param>
/// <param name="CookieHeader">
/// 可直接用于请求的 Cookie 头（B 站返回的全部 Set-Cookie 原样拼接）。
/// 仅在与 B 站域名通信时附加，禁止拼进 URL、禁止整体写日志。
/// </param>
public sealed record BiliLoginCredentials(
    string SessData,
    string BiliJct,
    string DedeUserId,
    string CookieHeader)
{
    /// <summary>三个关键 Cookie 是否齐备（齐备即可判定为已登录）。</summary>
    public bool IsComplete =>
        !string.IsNullOrEmpty(SessData) &&
        !string.IsNullOrEmpty(BiliJct) &&
        !string.IsNullOrEmpty(DedeUserId);
}
