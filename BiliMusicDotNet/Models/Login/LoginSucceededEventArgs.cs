using System;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 登录成功事件参数。
/// </summary>
/// <param name="profile">用户信息；资料接口失败时为 <see langword="null"/>。</param>
public sealed class LoginSucceededEventArgs(BiliUserProfile? profile) : EventArgs
{
    /// <summary>用户信息（昵称、头像）。</summary>
    public BiliUserProfile? Profile { get; } = profile;
}
