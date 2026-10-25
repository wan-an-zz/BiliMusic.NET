using System;

namespace BiliMusicDotNet.Models.Settings;

/// <summary>
/// 设置项所属的功能分区。
/// </summary>
/// <remarks>
/// 分区的显示名与顺序写在 <see cref="SettingSectionAttribute"/> 上，由设置服务读取后分组排序。
/// </remarks>
public enum SettingSection
{
    /// <summary>登录相关设置。</summary>
    [SettingSectionAttribute("登录设置", 0)]
    Login,

    /// <summary>关于页相关设置。</summary>
    [SettingSectionAttribute("关于设置", 1)]
    About,
}

/// <summary>
/// 为 <see cref="SettingSection"/> 的成员提供显示名与排序权重。
/// </summary>
/// <param name="title">分区显示名。</param>
/// <param name="order">分区排序权重，升序。</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SettingSectionAttribute(string title, int order) : Attribute
{
    /// <summary>分区显示名。</summary>
    public string Title { get; } = title;

    /// <summary>分区排序权重，升序。</summary>
    public int Order { get; } = order;
}