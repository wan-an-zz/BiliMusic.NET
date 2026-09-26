namespace BiliMusicDotNet.ViewModels.Messages;

/// <summary>
/// 二维码已失效、用户要求换一张：「关于」页收到后重走一次登录流程。
/// </summary>
/// <remarks>
/// 二维码那侧不自己重新取码，是因为弹窗归「关于」页管——
/// 换码要先把当前弹窗收掉再开新的，而弹窗管理器同一时刻只允许一个。
/// </remarks>
public sealed record ReLoginRequestMessage;
