using System.Text.Json.Serialization;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 登录成功后可用的凭据（与写操作所需的 CSRF 令牌），同时也是凭据落盘时序列化的模型。
/// </summary>
/// <remarks>
/// record 没有无参构造，反序列化只能走参数化构造函数，因此参数名与 JSON 字段名必须保持一致。
/// </remarks>
/// <param name="SessData">登录凭据，等价于账号登录态，持久化为明文，禁止写日志。</param>
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
    /// <summary>三个关键 Cookie 是否齐备（齐备即可判定为已登录）；派生值，不落盘。</summary>
    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrEmpty(SessData) &&
        !string.IsNullOrEmpty(BiliJct) &&
        !string.IsNullOrEmpty(DedeUserId);
}
