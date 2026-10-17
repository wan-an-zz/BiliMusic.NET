using Avalonia.Headless.XUnit;
using Avalonia.Platform;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// Headless 宿主自检：确认 [AvaloniaFact] 能跑，且 <c>avares://BiliMusicDotNet/...</c> 资产能被真实加载。
/// </summary>
public class HeadlessHostTests
{
    [AvaloniaFact]
    public void 占位图资产能被加载()
    {
        var uri = new Uri("avares://BiliMusicDotNet/Assets/图标占位图.png");

        Assert.True(AssetLoader.Exists(uri), $"资产不存在：{uri}");

        using var stream = AssetLoader.Open(uri);
        Assert.True(stream.Length > 0);
    }

    [AvaloniaFact]
    public void 应用资源里有导航图标()
    {
        var app = Avalonia.Application.Current;
        Assert.NotNull(app);
        Assert.True(app!.TryGetResource("IconAbout", null, out var about));
        Assert.NotNull(about);
    }
}
