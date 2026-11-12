using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 播放地址解析：请求特征、音质选择、地址规整、缓存与错误出口。
/// </summary>
public sealed class MusicUrlTests
{
    [Fact]
    public async Task 解析_选择第一个分片并回填元数据()
    {
        var (service, _, _) = Create();

        var info = await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);

        Assert.Equal(MusicResponses.HighUrl, info.Url.ToString());
        Assert.Equal([MusicResponses.HighBackupUrl], info.BackupUrls.Select(uri => uri.ToString()));
        Assert.Equal(64, info.Quality);
        Assert.Equal("mp4720", info.Format);
        Assert.Equal(MusicResponses.Title, info.Track.Title);
        Assert.Equal(MusicResponses.Owner, info.Track.OwnerName);
        Assert.Equal(TimeSpan.FromSeconds(MusicResponses.DurationSeconds), info.Track.Duration);
    }

    [Fact]
    public async Task 解析_请求带RefererUA与登录Cookie()
    {
        var credentials = new FakeCredentialStore();
        await credentials.SaveAsync(
            new BiliLoginCredentials("sess", "jct", "123", "SESSDATA=sess; bili_jct=jct"),
            TestContext.Current.CancellationToken);
        var (service, _, handler) = Create(credentials: credentials);

        await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.StartsWith("https://api.bilibili.com/", request.Uri.AbsoluteUri, StringComparison.Ordinal);
            Assert.StartsWith("https://www.bilibili.com", request.Referer, StringComparison.Ordinal);
            Assert.Contains("Chrome/", request.UserAgent ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal("SESSDATA=sess; bili_jct=jct", request.Cookie);
        }
    }

    [Fact]
    public async Task 解析_无凭据时不带Cookie()
    {
        var (service, _, handler) = Create();

        await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);

        Assert.All(handler.Requests, request => Assert.Null(request.Cookie));
    }

    [Fact]
    public async Task 解析_协议相对与http地址升级为https()
    {
        var segment = """
            {"order":1,"url":"//cdn.example/a.mp4","backup_url":["http://cdn.example/b.mp4"]}
            """;
        var (service, _, _) = Create(uri => uri.Contains("/x/web-interface/view", StringComparison.Ordinal)
            ? MusicResponses.View()
            : Responses.Json(MusicResponses.PlayUrlBody(segment)));

        var info = await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);

        Assert.Equal("https://cdn.example/a.mp4", info.Url.ToString());
        Assert.Equal("https://cdn.example/b.mp4", Assert.Single(info.BackupUrls).ToString());
    }

    [Fact]
    public async Task 解析_没有音频流时抛业务异常()
    {
        var playUrl = Responses.Json("""{"code":0,"message":"0","ttl":1,"data":{"quality":64,"format":"mp4720","durl":[]}}""");
        var (service, _, _) = Create(uri => uri.Contains("/x/web-interface/view", StringComparison.Ordinal)
            ? MusicResponses.View()
            : playUrl);

        var exception = await Assert.ThrowsAsync<BiliApiException>(
            () => service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken));

        Assert.Contains("音频", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解析_业务码非零时抛业务异常()
    {
        var view = Responses.Json("""{"code":-404,"message":"啥都木有","ttl":1,"data":null}""");
        var (service, _, _) = Create(uri => uri.Contains("/x/web-interface/view", StringComparison.Ordinal)
            ? view
            : MusicResponses.PlayUrl());

        var exception = await Assert.ThrowsAsync<BiliApiException>(
            () => service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken));

        Assert.Equal(-404, exception.Code);
        Assert.Contains("啥都木有", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解析_缺少cid时抛业务异常()
    {
        var (service, _, _) = Create(uri => uri.Contains("/x/web-interface/view", StringComparison.Ordinal)
            ? Responses.Json("""{"code":0,"message":"0","ttl":1,"data":{"cid":0,"title":"x"}}""")
            : MusicResponses.PlayUrl());

        await Assert.ThrowsAsync<BiliApiException>(
            () => service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 解析_非JSON响应转为音乐播放异常()
    {
        var (service, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>blocked</html>", Encoding.UTF8, "text/html"),
        });

        await Assert.ThrowsAsync<MusicPlaybackException>(
            () => service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 解析_缓存命中时只请求一次()
    {
        var (service, _, handler) = Create();

        await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);
        await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task 解析_缓存时长零时每次重新请求()
    {
        var (service, _, handler) = Create(cacheTtl: TimeSpan.Zero);

        await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);
        await service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task 解析_已取消的令牌直接取消()
    {
        var (service, _, _) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetPlaybackUrlAsync(MusicResponses.Bv1, cts.Token));
    }

    [Fact]
    public async Task 解析_并发请求同一BV只发一次view()
    {
        var handler = new BlockingViewHandler();
        using var service = new MusicService(
            handler,
            new FakeAudioEngine(),
            new FakeCredentialStore(),
            NullLogger<MusicService>.Instance,
            TimeSpan.FromMinutes(5));

        var first = service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);
        await handler.ViewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = service.GetPlaybackUrlAsync(MusicResponses.Bv1, TestContext.Current.CancellationToken);
        handler.Release.TrySetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.ViewCount);
        Assert.All(results, info => Assert.Equal(MusicResponses.HighUrl, info.Url.ToString()));
    }

    [Fact]
    public void BASS请求串_包含Referer与UA且不含Cookie()
    {
        var request = BassAudioEngine.BuildUrlRequest("https://cdn.example/a.m4s");

        Assert.StartsWith("https://cdn.example/a.m4s\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Referer: https://www.bilibili.com\r\n", request, StringComparison.Ordinal);
        Assert.Contains("User-Agent: ", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", request, StringComparison.OrdinalIgnoreCase);
    }

    private static (MusicService Service, FakeAudioEngine Audio, FakeBiliHandler Handler) Create(
        Func<string, HttpResponseMessage>? responder = null,
        TimeSpan? cacheTtl = null,
        FakeCredentialStore? credentials = null)
    {
        var handler = new FakeBiliHandler(responder ?? MusicResponses.Router());
        var audio = new FakeAudioEngine();
        var service = new MusicService(
            handler,
            audio,
            credentials ?? new FakeCredentialStore(),
            NullLogger<MusicService>.Instance,
            cacheTtl ?? TimeSpan.FromMinutes(5));
        return (service, audio, handler);
    }

    private sealed class BlockingViewHandler : HttpMessageHandler
    {
        private int _viewCount;

        public TaskCompletionSource ViewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ViewCount => Volatile.Read(ref _viewCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("view", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _viewCount);
                ViewStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return MusicResponses.View();
            }

            return MusicResponses.PlayUrl();
        }
    }
}
