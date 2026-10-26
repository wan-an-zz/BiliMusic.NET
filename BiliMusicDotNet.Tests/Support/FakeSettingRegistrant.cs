using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Settings;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 可配置的 <see cref="ISetting"/>：记录收到的变更，并按需在注册或接收时抛异常。
/// </summary>
public sealed class FakeSettingRegistrant(params SettingItem[] items) : ISetting
{
    /// <summary>默认返回的设置项。</summary>
    public IReadOnlyList<SettingItem> Items { get; } = items;

    /// <summary>按调用顺序记录收到的设置项。</summary>
    public List<SettingItem> Received { get; } = [];

    /// <summary>非空时替代 <see cref="Items"/> 作为注册结果。</summary>
    public Func<IEnumerable<SettingItem>>? RegisterOverride { get; init; }

    /// <summary>非空时由 <see cref="RegisterSettings"/> 抛出。</summary>
    public Exception? RegisterException { get; init; }

    /// <summary>非空时由 <see cref="ReceiveSettingChanges"/> 抛出。</summary>
    public Exception? ReceiveException { get; init; }

    /// <inheritdoc />
    public IEnumerable<SettingItem> RegisterSettings()
    {
        if (RegisterException is not null)
            throw RegisterException;

        return RegisterOverride?.Invoke() ?? Items;
    }

    /// <inheritdoc />
    public void ReceiveSettingChanges(SettingItem item)
    {
        Received.Add(item);

        if (ReceiveException is not null)
            throw ReceiveException;
    }
}
