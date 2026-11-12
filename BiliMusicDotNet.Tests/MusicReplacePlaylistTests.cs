using System.Net.Http;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Tests.Support;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 整表替换：先停流再换列表、自动播放第一首、空列表与失败出口。
/// </summary>
public sealed class MusicReplacePlaylistTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 整表替换_清空旧列表并按新顺序载入()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await host.Service.ReplacePlaylistAsync(
            [MusicResponses.Bv3, MusicResponses.Bv4],
            Ct);

        Assert.Equal([MusicResponses.Bv3, MusicResponses.Bv4], Bvids(host));
        Assert.Equal(MusicResponses.Bv3, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 替换时立即停止并释放当前流()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        var generationBefore = host.Audio.Generation;

        await host.Service.ReplacePlaylistAsync([MusicResponses.Bv2], Ct);

        Assert.True(host.Audio.Generation > generationBefore);
        Assert.True(host.Audio.HasStream);
        Assert.Equal([MusicResponses.Bv2], Bvids(host));
        Assert.Single(host.Service.Playlist);
    }

    [Fact]
    public async Task 输入去重_保留首次出现顺序()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        await host.Service.ReplacePlaylistAsync(
            [MusicResponses.Bv3, MusicResponses.Bv3, MusicResponses.Bv4],
            Ct);

        Assert.Equal([MusicResponses.Bv3, MusicResponses.Bv4], Bvids(host));
        Assert.Equal(2, host.Audio.Loads.Count);
        Assert.Equal(MusicResponses.HighUrl, host.Audio.LoadedUrls[1]);
    }

    [Fact]
    public async Task 空列表替换_停在Idle且Current为空()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        await host.Service.ReplacePlaylistAsync([], Ct);

        Assert.Empty(host.Service.Playlist);
        Assert.Null(host.Service.Current);
        Assert.Equal(MusicPlaybackState.Idle, host.Service.State);
        Assert.False(host.Audio.HasStream);
    }

    [Fact]
    public async Task 替换后旧流的迟到结束事件被忽略()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        var oldGeneration = host.Audio.Generation;
        await host.Service.ReplacePlaylistAsync([MusicResponses.Bv2], Ct);

        await host.Service.HandlePlaybackEndedAsync(oldGeneration);

        Assert.Equal(MusicResponses.Bv2, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 替换后预取重置为新列表的下一首()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await host.Service.ReplacePlaylistAsync(
            [MusicResponses.Bv3, MusicResponses.Bv4],
            Ct);

        await MusicTestKit.WaitUntilAsync(
            () => MusicTestKit.RequestCountFor(host.Handler, MusicResponses.Bv4) >= 2,
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task 第一首加载失败_列表保留并触发失败事件()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);
        host.Audio.LoadFailures.Enqueue(new MusicPlaybackException("boom"));
        MusicPlaybackFailedEventArgs? failed = null;
        host.Service.PlaybackFailed += (_, args) => failed = args;

        await host.Service.ReplacePlaylistAsync(
            [MusicResponses.Bv2, MusicResponses.Bv3],
            Ct);

        Assert.Equal([MusicResponses.Bv2, MusicResponses.Bv3], Bvids(host));
        Assert.Null(host.Service.Current);
        Assert.Equal(MusicPlaybackState.Failed, host.Service.State);
        Assert.NotNull(failed);
        Assert.Equal(MusicResponses.Bv2, failed.Bvid);
    }

    [Fact]
    public async Task 替换空列表后预取被取消()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await host.Service.ReplacePlaylistAsync([], Ct);

        Assert.False(host.Service.HasPendingPrefetch);
        Assert.Null(host.Service.PrefetchBvid);
    }

    private static string[] Bvids(MusicTestHost host)
        => host.Service.Playlist.Select(track => track.Bvid).ToArray();
}
