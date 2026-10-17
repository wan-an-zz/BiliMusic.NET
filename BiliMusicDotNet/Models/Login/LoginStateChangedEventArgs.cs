using System;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 登录状态变化事件参数。
/// </summary>
/// <param name="state">变化后的状态。</param>
/// <param name="message">面向用户的说明文本。</param>
/// <param name="profile">已登录时携带用户信息，否则为 <see langword="null"/>。</param>
public sealed class LoginStateChangedEventArgs(LoginState state, string message, BiliUserProfile? profile) : EventArgs
{
    /// <summary>变化后的状态。</summary>
    public LoginState State { get; } = state;

    /// <summary>面向用户的说明文本，可直接展示在登录界面上。</summary>
    public string Message { get; } = message;

    /// <summary>已登录时携带用户信息。</summary>
    public BiliUserProfile? Profile { get; } = profile;
}
