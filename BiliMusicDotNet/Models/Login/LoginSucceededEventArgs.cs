using System;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 登录成功事件参数。
/// </summary>
/// <param name="credentials">本次登录取得的凭据。</param>
/// <param name="profile">用户信息；资料接口失败时为 <see langword="null"/>。</param>
public sealed class LoginSucceededEventArgs(BiliLoginCredentials credentials, BiliUserProfile? profile) : EventArgs
{
    /// <summary>本次登录取得的凭据。</summary>
    public BiliLoginCredentials Credentials { get; } = credentials;

    /// <summary>用户信息（昵称、头像）。</summary>
    public BiliUserProfile? Profile { get; } = profile;
}
