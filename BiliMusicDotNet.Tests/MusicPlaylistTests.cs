using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Tests.Support;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 播放列表的基础操作：插入顺序、去重移动、当前曲目保护与自动播放边界。
/// </summary>
public sealed class MusicPlaylistTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 插入尾部_保持输入顺序并自动播放第一首()
    {
        using var host = MusicTestKit.Create();

        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        Assert.Equal([MusicResponses.Bv1, MusicResponses.Bv2], Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 插入首部_保持输入顺序()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv2, MusicResponses.Bv3],
            isBeginning: true,
            cancellationToken: Ct);

        Assert.Equal(
            [MusicResponses.Bv2, MusicResponses.Bv3, MusicResponses.Bv1],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
    }

    [Fact]
    public async Task 重复BV_移动到目标位置且不产生重复()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);

        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv2],
            isBeginning: true,
            cancellationToken: Ct);

        Assert.Equal(
            [MusicResponses.Bv2, MusicResponses.Bv1, MusicResponses.Bv3],
            Bvids(host));

        await host.Service.InsertMusicsAsync([MusicResponses.Bv2], cancellationToken: Ct);

        Assert.Equal(
            [MusicResponses.Bv1, MusicResponses.Bv3, MusicResponses.Bv2],
            Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 当前曲目被移动_播放不中断()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        Assert.Equal([MusicResponses.Bv2, MusicResponses.Bv1], Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
        Assert.Equal(1, host.Audio.PlayCount);
    }

    [Fact]
    public async Task 仅在Current为空时自动播放()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        await host.Service.InsertMusicsAsync([MusicResponses.Bv2], cancellationToken: Ct);

        Assert.Equal([MusicResponses.Bv1, MusicResponses.Bv2], Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Single(host.Audio.Loads);
        Assert.Equal(1, host.Audio.PlayCount);
    }

    [Fact]
    public async Task 输入重复BV_只保留一份()
    {
        using var host = MusicTestKit.Create();

        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        Assert.Equal([MusicResponses.Bv1, MusicResponses.Bv2], Bvids(host));
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 插入后回填标题与UP主()
    {
        using var host = MusicTestKit.Create();

        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        Assert.Equal(MusicResponses.Title, host.Service.Playlist[0].Title);
        Assert.Equal(MusicResponses.Owner, host.Service.Playlist[0].OwnerName);
        Assert.Equal(
            TimeSpan.FromSeconds(MusicResponses.DurationSeconds),
            host.Service.Playlist[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(MusicResponses.DurationSeconds), host.Service.Duration);
    }

    [Fact]
    public async Task 删除非当前曲目_列表更新且播放不中断()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await host.Service.RemoveMusicsAsync([MusicResponses.Bv2], Ct);

        Assert.Equal([MusicResponses.Bv1], Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
        Assert.Equal(MusicPlaybackState.Playing, host.Service.State);
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 删除当前曲目_抛异常且整批不执行()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Service.RemoveMusicsAsync(
                [MusicResponses.Bv1, MusicResponses.Bv2],
                Ct));

        Assert.Contains("当前", exception.Message, StringComparison.Ordinal);
        Assert.Equal([MusicResponses.Bv1, MusicResponses.Bv2], Bvids(host));
        Assert.Equal(MusicResponses.Bv1, host.Service.Current?.Bvid);
    }

    [Fact]
    public async Task 空白BV_在校验阶段抛异常且不修改列表()
    {
        using var host = MusicTestKit.Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => host.Service.InsertMusicsAsync([MusicResponses.Bv1, "  "], cancellationToken: Ct));

        Assert.Empty(host.Service.Playlist);
        Assert.Empty(host.Audio.Loads);
    }

    [Fact]
    public async Task 空集合_不抛异常也不改变状态()
    {
        using var host = MusicTestKit.Create();

        await host.Service.InsertMusicsAsync([], cancellationToken: Ct);
        await host.Service.RemoveMusicsAsync([], Ct);

        Assert.Empty(host.Service.Playlist);
        Assert.Null(host.Service.Current);
        Assert.Equal(MusicPlaybackState.Idle, host.Service.State);
    }

    [Fact]
    public async Task 集合为null_抛ArgumentNullException()
    {
        using var host = MusicTestKit.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => host.Service.InsertMusicsAsync(null!, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => host.Service.ReplacePlaylistAsync(null!, Ct));
    }

    private static string[] Bvids(MusicTestHost host)
        => host.Service.Playlist.Select(track => track.Bvid).ToArray();
}
