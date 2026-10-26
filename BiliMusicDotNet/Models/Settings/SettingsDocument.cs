using System.Collections.Generic;
using System.Text.Json;

namespace BiliMusicDotNet.Models.Settings;

/// <summary>
/// <c>settings.json</c> 的根对象；只保存身份与值，其余元数据以代码注册为准。
/// </summary>
internal sealed class SettingsDocument
{
    public int Version { get; set; } = 1;

    public List<SettingsEntry> Items { get; set; } = [];
}

/// <summary>
/// 一条持久化记录；未知身份原样保留，写回时追加到已知项之后。
/// </summary>
internal sealed class SettingsEntry
{
    public string? Type { get; set; }

    public string? Name { get; set; }

    public JsonElement Value { get; set; }
}
