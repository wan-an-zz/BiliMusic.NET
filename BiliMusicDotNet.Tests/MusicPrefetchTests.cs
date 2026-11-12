using System.Net.Http;
using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 异步预取：只解析地址不建流、随列表变化重算、失败不打扰用户、播放时复用缓存。
/// </summary>
public sealed class MusicPrefetchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task 自动播放后预取下一首且不建流()
    {
        using var host = MusicTestKit.Create();

        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await MusicTestKit.WaitUntilAsync(
            () => MusicTestKit.RequestCountFor(host.Handler, MusicResponses.Bv2) >= 2,
            TimeSpan.FromSeconds(5));
        Assert.Single(host.Audio.Loads);
    }

    [Fact]
    public async Task 删除下一首后_预取新的下一首()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2, MusicResponses.Bv3],
            cancellationToken: Ct);
        await MusicTestKit.WaitUntilAsync(
            () => MusicTestKit.RequestCountFor(host.Handler, MusicResponses.Bv2) >= 2,
            TimeSpan.FromSeconds(5));

        await host.Service.RemoveMusicsAsync([MusicResponses.Bv2], Ct);

        await MusicTestKit.WaitUntilAsync(
            () => MusicTestKit.RequestCountFor(host.Handler, MusicResponses.Bv3) >= 2,
            TimeSpan.FromSeconds(5));
        Assert.Equal([MusicResponses.Bv1, MusicResponses.Bv3], Bvids(host));
    }

    [Fact]
    public async Task 预取完成后回填列表元数据()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await MusicTestKit.WaitUntilAsync(
            () => host.Service.Playlist[1].Title == MusicResponses.Title,
            TimeSpan.FromSeconds(5));
        Assert.Equal(MusicResponses.Owner, host.Service.Playlist[1].OwnerName);
    }

    [Fact]
    public async Task 预取失败_不触发用户可见失败事件()
    {
        using var host = MusicTestKit.Create(FailingFor(MusicResponses.Bv2));
        var failed = false;
        host.Service.PlaybackFailed += (_, _) => failed = true;

        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);

        await MusicTestKit.WaitUntilAsync(
            () => MusicTestKit.RequestCountFor(host.Handler, MusicResponses.Bv2) >= 1,
            TimeSpan.FromSeconds(5));
        await MusicTestKit.WaitUntilAsync(
            () => !host.Service.HasPendingPrefetch,
            TimeSpan.FromSeconds(5));
        Assert.False(failed);
    }

    [Fact]
    public async Task 播放下一首_复用预取缓存()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: Ct);
        await MusicTestKit.WaitUntilAsync(
            () => MusicTestKit.RequestCountFor(host.Handler, MusicResponses.Bv2) >= 2,
            TimeSpan.FromSeconds(5));
        var before = host.Handler.Requests.Count;

        Assert.True(await host.Service.PlayNextAsync(Ct));

        Assert.Equal(before, host.Handler.Requests.Count);
        Assert.Equal(2, host.Audio.Loads.Count);
    }

    [Fact]
    public async Task 尾曲没有下一首_不发起预取()
    {
        using var host = MusicTestKit.Create();
        await host.Service.InsertMusicsAsync([MusicResponses.Bv1], cancellationToken: Ct);

        await Task.Delay(100, Ct);

        Assert.Null(host.Service.PrefetchBvid);
        Assert.False(host.Service.HasPendingPrefetch);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    private static Func<string, HttpResponseMessage> FailingFor(string bvid)
        => uri => uri.Contains("bvid=" + bvid, StringComparison.Ordinal)
            ? Responses.Json("""{"code":-404,"message":"not found","ttl":1,"data":null}""")
            : MusicResponses.Router()(uri);

    private static string[] Bvids(MusicTestHost host)
        => host.Service.Playlist.Select(track => track.Bvid).ToArray();
}
