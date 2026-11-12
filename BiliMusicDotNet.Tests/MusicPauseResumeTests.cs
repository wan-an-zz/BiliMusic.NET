using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Tests.Support;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 暂停与恢复：正常恢复不重建流；连接中断后按保存的字节位置重建。
/// </summary>
public sealed class MusicPauseResumeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 暂停期间流结束_恢复时按保存位置重建()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        host.Audio.Position = TimeSpan.FromSeconds(30);
        host.Audio.PositionBytes = 5000;
        Assert.True(await host.Service.PauseAsync(Ct));
        host.Audio.MarkEnded();

        Assert.True(await host.Service.ResumeAsync(Ct));

        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
        Assert.Equal(5000, host.Audio.Loads[1].OffsetBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), host.Audio.Loads[1].BasePosition);
        Assert.Equal(2, host.Audio.PlayCount);
    }

    [Fact]
    public async Task 偏移重建失败_从头播放()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        host.Audio.Position = TimeSpan.FromSeconds(30);
        host.Audio.PositionBytes = 5000;
        Assert.True(await host.Service.PauseAsync(Ct));
        host.Audio.MarkEnded();
        host.Audio.LoadFailures.Enqueue(new MusicPlaybackException("offset unsupported"));

        Assert.True(await host.Service.ResumeAsync(Ct));

        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
        Assert.Equal(0, host.Audio.Loads[1].OffsetBytes);
        Assert.Equal(TimeSpan.Zero, host.Audio.Loads[1].BasePosition);
    }

    [Fact]
    public async Task 暂停期间收到结束事件_保持暂停且不切歌()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        Assert.True(await host.Service.PauseAsync(Ct));

        host.Audio.RaiseEnded();
        await Task.Delay(100, Ct);

        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Paused, host.Service.State);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 暂停时再次暂停返回true()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        Assert.True(await host.Service.PauseAsync(Ct));
        Assert.True(await host.Service.PauseAsync(Ct));
        Assert.Equal(MusicPlaybackState.Paused, host.Service.State);
    }
}
