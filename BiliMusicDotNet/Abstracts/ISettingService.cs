using System;
using System.Collections.Generic;
using BiliMusicDotNet.Models.Settings;

namespace BiliMusicDotNet.Abstracts;

/// <summary>
/// 设置的读取与变更入口。
/// </summary>
public interface ISettingService
{
    /// <summary>
    /// 取启动时装载好的分区设置；重复调用不重新读盘。
    /// </summary>
    IReadOnlyList<SettingSectionGroup> GetAllSettings();

    /// <summary>
    /// 保存一项设置：通知注册方，并把当前值写回磁盘。
    /// </summary>
    /// <param name="item">发生变化的设置项。</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> 为 <see langword="null"/>。</exception>
    void ChangeSettings(SettingItem item);
}
