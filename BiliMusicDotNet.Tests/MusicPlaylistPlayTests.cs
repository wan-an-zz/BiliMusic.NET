using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Tests.Support;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 播放列表内指定曲目：下一首走先切歌后移动，其余走先移动后播放。
/// </summary>
public sealed class MusicPlaylistPlayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 选中下一首_先切歌再移到第一位()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);

        Assert.True(await host.Service.PlayPlaylistMusicAsync(MusicResponses.Bv2, Ct));

        Assert.Equal(
            [MusicResponses.Bv2, MusicResponses.Bv1, MusicResponses.Bv3],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
        // 下一首分支内部已经 Resume，不能再多一次 Play。
        Assert.Equal(2, host.Audio.PlayCount);
    }

    [Fact]
    public async Task 选中非下一首_先移到第一位再播放()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);

        Assert.True(await host.Service.PlayPlaylistMusicAsync(MusicResponses.Bv3, Ct));

        Assert.Equal(
            [MusicResponses.Bv3, MusicResponses.Bv1, MusicResponses.Bv2],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 选中当前曲目_不重启播放()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        Assert.True(await host.Service.PlayPlaylistMusicAsync(MusicResponses.Bv1, Ct));

        Assert.Equal([MusicResponses.Bv1, MusicResponses.Bv2], Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
        Assert.Equal(1, host.Audio.PlayCount);
    }

    [Fact]
    public async Task 选中不存在的曲目_抛ArgumentException()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => host.Service.PlayPlaylistMusicAsync("BV1notInList", Ct));

        Assert.Equal("bvid", exception.ParamName);
        Assert.Equal([MusicResponses.Bv1], Bvids(host));
    }

    [Fact]
    public async Task 下一首加载失败_不移动列表()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        host.Audio.LoadFailures.Enqueue(new BiliMusicDotNet.Models.Exceptions.MusicPlaybackException("boom"));

        Assert.False(await host.Service.PlayPlaylistMusicAsync(MusicResponses.Bv2, Ct));

        Assert.Equal([MusicResponses.Bv1, MusicResponses.Bv2], Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
    }

    [Fact]
    public async Task 非下一首加载失败_列表已按要求移动()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);
        host.Audio.LoadFailures.Enqueue(new BiliMusicDotNet.Models.Exceptions.MusicPlaybackException("boom"));

        Assert.False(await host.Service.PlayPlaylistMusicAsync(MusicResponses.Bv3, Ct));

        Assert.Equal(
            [MusicResponses.Bv3, MusicResponses.Bv1, MusicResponses.Bv2],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
    }

    private static string[] Bvids(MusicTestHost host)
        => host.Service.Playlist.Select(track => track.Bvid).ToArray();
}
