using System.Collections.Generic;
using BiliMusicDotNet.Models.Settings;

namespace BiliMusicDotNet.Abstracts;

/// <summary>
/// 设置注册方：声明本模块的设置项，并接收变更。
/// </summary>
public interface ISetting
{
    /// <summary>
    /// 返回本模块的设置项；每项的 <see cref="SettingItem.Value"/> 是默认值。
    /// </summary>
    IEnumerable<SettingItem> RegisterSettings();

    /// <summary>
    /// 接收一项设置的最新值；启动装载与用户修改都会调用。
    /// </summary>
    /// <param name="item">发生变化的设置项，其 <see cref="SettingItem.Type"/> 指向本注册方。</param>
    void ReceiveSettingChanges(SettingItem item);
}
