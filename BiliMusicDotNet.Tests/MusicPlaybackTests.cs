using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Tests.Support;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 播放控制：暂停继续、上下首、选中、任意曲目播放与三种插入播放位置。
/// </summary>
public sealed class MusicPlaybackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void 初始状态为Idle()
    {
        using var host = MusicTestKit.Create();

        Assert.Empty(host.Service.Playlist);
        Assert.Null(host.Service.Current);
        Assert.Equal(MusicPlaybackState.Idle, host.Service.State);
        Assert.Equal(TimeSpan.Zero, host.Service.Position);
        Assert.Equal(TimeSpan.Zero, host.Service.Duration);
    }

    [Fact]
    public async Task 没有当前曲目时_控制方法返回false()
    {
        using var host = MusicTestKit.Create();

        Assert.False(await host.Service.PauseAsync(Ct));
        Assert.False(await host.Service.ResumeAsync(Ct));
        Assert.False(await host.Service.PlayNextAsync(Ct));
        Assert.False(await host.Service.PlayPreviousAsync(Ct));
        Assert.False(await host.Service.TogglePlayPauseAsync(Ct));
    }

    [Fact]
    public async Task 暂停与继续_保留位置且不重建流()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        host.Audio.Position = TimeSpan.FromSeconds(42);
        host.Audio.PositionBytes = 12345;

        Assert.True(await host.Service.PauseAsync(Ct));
        Assert.Equal(MusicPlaybackState.Paused, host.Service.State);
        Assert.Equal(TimeSpan.FromSeconds(42), host.Service.Position);

        Assert.True(await host.Service.ResumeAsync(Ct));
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
        Assert.Equal(TimeSpan.FromSeconds(42), host.Service.Position);
    }

    [Fact]
    public async Task 播放中切换暂停与继续()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        Assert.True(await host.Service.TogglePlayPauseAsync(Ct));
        Assert.Equal(MusicPlaybackState.Paused, host.Service.State);

        Assert.True(await host.Service.TogglePlayPauseAsync(Ct));
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.PlayCount);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 上一首与下一首_按顺序且首尾不循环()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);

        Assert.True(await host.Service.PlayNextAsync(Ct));
        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);

        Assert.True(await host.Service.PlayNextAsync(Ct));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);

        Assert.False(await host.Service.PlayNextAsync(Ct));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);

        Assert.True(await host.Service.PlayPreviousAsync(Ct));
        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);

        Assert.True(await host.Service.PlayPreviousAsync(Ct));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);

        Assert.False(await host.Service.PlayPreviousAsync(Ct));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
    }

    [Fact]
    public async Task 选中曲目_只准备播放流不开始播放()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        Assert.True(await host.Service.SelectMusicAsync(MusicResponses.Bv2, Ct));

        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Paused, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
        Assert.Equal(MusicPlaybackState.Stopped, host.Audio.State);
        Assert.Equal(1, host.Audio.PlayCount);

        Assert.True(await host.Service.ResumeAsync(Ct));
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
        Assert.Equal(2, host.Audio.PlayCount);
    }

    [Fact]
    public async Task 选中不在列表中的曲目_返回false且状态不变()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        Assert.False(await host.Service.SelectMusicAsync("BV1notInList", Ct));

        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 播放列表外的曲目_插入首位并播放()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        Assert.True(await host.Service.PlayMusicAsync(MusicResponses.Bv2, Ct));

        Assert.Equal([MusicResponses.Bv2, MusicResponses.Bv1], Bvids(host));
        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 播放已在列表的曲目_移动到首位()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);

        Assert.True(await host.Service.PlayMusicAsync(MusicResponses.Bv3, Ct));

        Assert.Equal(
            [MusicResponses.Bv3, MusicResponses.Bv1, MusicResponses.Bv2],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
    }

    [Fact]
    public async Task 插入并播放_列表首位()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        Assert.True(await host.Service.InsertAndPlayAsync(
            [MusicResponses.Bv3],
            PlaylistInsertPosition.Beginning,
            Ct));

        Assert.Equal(
            [MusicResponses.Bv3, MusicResponses.Bv1, MusicResponses.Bv2],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
    }

    [Fact]
    public async Task 插入并播放_列表末尾()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        Assert.True(await host.Service.InsertAndPlayAsync(
            [MusicResponses.Bv3],
            PlaylistInsertPosition.End,
            Ct));

        Assert.Equal(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);
    }

    [Fact]
    public async Task 插入并播放_当前曲目之后()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        Assert.True(await host.Service.InsertAndPlayAsync(
            [MusicResponses.Bv3],
            PlaylistInsertPosition.AfterCurrent,
            Ct));

        Assert.Equal(
            [MusicResponses.Bv1, MusicResponses.Bv3, MusicResponses.Bv2],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);
    }

    [Fact]
    public async Task 加载下一首失败_保留旧曲目并触发失败事件()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        host.Audio.LoadFailures.Enqueue(new MusicPlaybackException("boom"));
        MusicPlaybackFailedEventArgs? failed = null;
        host.Service.PlaybackFailed += (_, args) => failed = args;

        Assert.False(await host.Service.PlayNextAsync(Ct));

        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
        Assert.NotNull(failed);
        Assert.Equal(MusicResponses.Bv2, failed.Bvid);
    }

    [Fact]
    public async Task Duration优先使用曲目元数据()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        host.Audio.Duration = TimeSpan.FromSeconds(999);

        Assert.Equal(TimeSpan.FromSeconds(MusicResponses.DurationSeconds), host.Service.Duration);
    }

    [Fact]
    public async Task Dispose后调用公开方法抛ObjectDisposedException()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        host.Service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => host.Service.InsertMusicsAsync([MusicResponses.Bv2], cancellationToken: Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => host.Service.PlayNextAsync(Ct));
    }

    private static string[] Bvids(MusicTestHost host)
        => host.Service.Playlist.Select(track => track.Bvid).ToArray();
}
