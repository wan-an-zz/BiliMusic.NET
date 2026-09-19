namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 轮询的终局结果。
/// </summary>
/// <param name="Outcome">终局类型。</param>
/// <param name="Credentials">仅登录成功时非空。</param>
/// <param name="Profile">仅登录成功时非空，且资料接口可能失败（此时为 <see langword="null"/>）。</param>
/// <param name="Message">面向用户的说明文本。</param>
public sealed record QrLoginResult(
    QrLoginOutcome Outcome,
    BiliLoginCredentials? Credentials,
    BiliUserProfile? Profile,
    string Message);
