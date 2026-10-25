namespace BiliMusicDotNet.Models.Settings;

/// <summary>
/// 设置项的编辑方式，决定设置页生成哪种控件。
/// </summary>
public enum SettingMethod
{
    /// <summary>布尔开关。</summary>
    Toggle,

    /// <summary>从预设选项里选一个。</summary>
    SingleChoice,
}
