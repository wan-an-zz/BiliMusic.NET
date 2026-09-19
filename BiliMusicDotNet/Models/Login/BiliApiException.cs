using System;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// B 站接口返回非 0 业务码时抛出。
/// </summary>
/// <param name="code">B 站业务码（如 <c>-101</c> 未登录、<c>-412</c> 被风控拦截）。</param>
/// <param name="message">接口返回的提示文本。</param>
/// <param name="requestPath">出错的接口路径，便于定位。</param>
public sealed class BiliApiException(int code, string message, string requestPath)
    : Exception($"Bilibili API [{code}] {message}（{requestPath}）")
{
    /// <summary>B 站业务码。</summary>
    public int Code { get; } = code;

    /// <summary>出错的接口路径。</summary>
    public string RequestPath { get; } = requestPath;
}
