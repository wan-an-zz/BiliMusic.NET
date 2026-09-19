using System;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 当前登录用户的展示信息。
/// </summary>
/// <param name="Mid">用户 ID。</param>
/// <param name="Nickname">昵称。</param>
/// <param name="AvatarUri">头像地址（已把 <c>//</c> 与 <c>http://</c> 统一升为 <c>https://</c>）。</param>
/// <param name="IsVip">是否大会员（<c>vipStatus == 1</c>）。</param>
/// <param name="Level">用户等级，资料缺失时为 0。</param>
public sealed record BiliUserProfile(
    long Mid,
    string Nickname,
    Uri? AvatarUri,
    bool IsVip,
    int Level);
