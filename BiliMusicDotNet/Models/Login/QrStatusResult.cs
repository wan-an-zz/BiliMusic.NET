namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 一次扫码状态查询的结果。
/// </summary>
/// <param name="State">归一化后的状态。</param>
/// <param name="RawStatus">B 站原始状态码（<c>data.code</c>），未知状态时保留原值便于排查。</param>
/// <param name="Message">接口返回的提示文本（已剥除 <c>&amp;nbsp;</c> 等 HTML 残留），可直接展示。</param>
/// <param name="Credentials">仅当本次查询即登录成功时非空。</param>
public sealed record QrStatusResult(
    LoginState State,
    int RawStatus,
    string Message,
    BiliLoginCredentials? Credentials);
