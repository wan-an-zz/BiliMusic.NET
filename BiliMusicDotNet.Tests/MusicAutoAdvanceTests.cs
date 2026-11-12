using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Tests.Support;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 自然播完的自动切歌：代次、状态、尾曲停止与失败出口。
/// </summary>
public sealed class MusicAutoAdvanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 自然播完_自动切到下一首()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        var generation = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(generation);

        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 尾曲播完_停在Stopped且不循环()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        var generation = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(generation);

        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Stopped, host.Service.State);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 错误代次_忽略()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        var generation = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(generation - 1);

        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 暂停时收到结束事件_不自动切歌()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        Assert.True(await host.Service.PauseAsync(Ct));
        var generation = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(generation);

        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Paused, host.Service.State);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 选中未播放时收到结束事件_不自动切歌()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        Assert.True(await host.Service.SelectMusicAsync(MusicResponses.Bv2, Ct));
        var generation = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(generation);

        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Paused, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 下一首加载失败_触发失败事件且停在Stopped()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        host.Audio.LoadFailures.Enqueue(new MusicPlaybackException("boom"));
        MusicPlaybackFailedEventArgs? failed = null;
        host.Service.PlaybackFailed += (_, args) => failed = args;
        var generation = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(generation);

        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Stopped, host.Service.State);
        Assert.Single(host.Audio.Loads);
        Assert.NotNull(failed);
        Assert.Equal(MusicResponses.Bv2, failed.Bvid);
    }

    [Fact]
    public async Task 连续迟到事件_只切一首()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);
        var oldGeneration = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(oldGeneration);
        await host.Service.HandlePlaybackEndedAsync(oldGeneration);

        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 事件路径_自动切歌并回到Playing()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        host.Audio.RaiseEnded();

        await MusicTestKit.WaitUntilAsync(
            () => host.Service.Current?.Bvid == MusicResponses.Bv2
                  && host.Service.State == MusicPlaybackState.Playing,
            TimeSpan.FromSeconds(5));
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 尾曲事件路径_停在Stopped()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        host.Audio.RaiseEnded();

        await MusicTestKit.WaitUntilAsync(
            () => host.Service.State == MusicPlaybackState.Stopped,
            TimeSpan.FromSeconds(5));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
    }

    [Fact]
    public async Task 自动切歌后预取新的下一首()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);
        var generation = host.Audio.Generation;

        await host.Service.HandlePlaybackEndedAsync(generation);

        await MusicTestKit.WaitUntilAsync(
            () => MusicTestKit.RequestCountFor(host.Handler, MusicResponses.Bv3) >= 2,
            TimeSpan.FromSeconds(5));
    }
}
