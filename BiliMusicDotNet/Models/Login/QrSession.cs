using System;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 一次扫码登录会话。
/// </summary>
/// <param name="Key">轮询凭据（<c>qrcode_key</c>），业务层已保管，UI 一般不需要使用。</param>
/// <param name="Content">二维码内容字符串，需由 UI 渲染成二维码图片。</param>
/// <param name="ExpiresAt">本地推算的失效时刻（B 站二维码有效期约 3 分钟），UI 可用于画倒计时/进度。</param>
public sealed record QrSession(string Key, string Content, DateTimeOffset ExpiresAt);
