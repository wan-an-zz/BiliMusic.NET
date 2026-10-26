using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Settings;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 可配置的 <see cref="ISettingService"/>：返回固定分区，记录变更并按需抛异常。
/// </summary>
public sealed class FakeSettingService(params SettingSectionGroup[] groups) : ISettingService
{
    /// <summary>构造时返回的分区。</summary>
    public IReadOnlyList<SettingSectionGroup> Groups { get; } = groups;

    /// <summary>按调用顺序记录被保存的设置项。</summary>
    public List<SettingItem> ChangedItems { get; } = [];

    /// <summary>非空时由 <see cref="ChangeSettings"/> 抛出。</summary>
    public Exception? ChangeException { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<SettingSectionGroup> GetAllSettings() => Groups;

    /// <inheritdoc />
    public void ChangeSettings(SettingItem item)
    {
        ChangedItems.Add(item);

        if (ChangeException is not null)
            throw ChangeException;
    }
}
