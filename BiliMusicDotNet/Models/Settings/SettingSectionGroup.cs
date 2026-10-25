using System.Collections.Generic;

namespace BiliMusicDotNet.Models.Settings;

/// <summary>
/// 同一分区的设置项集合，供设置页直接绑定。
/// </summary>
public sealed record SettingSectionGroup(
    SettingSection Section,
    string Title,
    int Order,
    IReadOnlyList<SettingItem> Items);
