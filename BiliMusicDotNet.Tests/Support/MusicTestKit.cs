using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BiliMusicDotNet.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>音乐服务测试宿主：统一构造假 HTTP、假引擎与真实服务。</summary>
internal sealed class MusicTestHost : IDisposable
{
    public MusicTestHost(MusicService service, FakeAudioEngine audio, FakeBiliHandler handler)
    {
        Service = service;
        Audio = audio;
        Handler = handler;
    }

    public MusicService Service { get; }

    public FakeAudioEngine Audio { get; }

    public FakeBiliHandler Handler { get; }

    public void Dispose() => Service.Dispose();
}

/// <summary>音乐服务测试的构造与等待辅助。</summary>
internal static class MusicTestKit
{
    /// <summary>构造测试宿主。</summary>
    /// <param name="responder">HTTP 响应器；默认按 view/playurl 路径返回固定响应。</param>
    /// <param name="cacheTtl">地址缓存时长。</param>
    /// <param name="credentials">凭据服务。</param>
    /// <returns>测试宿主。</returns>
    public static MusicTestHost Create(
        Func<string, HttpResponseMessage>? responder = null,
        TimeSpan? cacheTtl = null,
        FakeCredentialStore? credentials = null)
    {
        var audio = new FakeAudioEngine();
        var handler = new FakeBiliHandler(responder ?? MusicResponses.Router());
        var service = new MusicService(
            handler,
            audio,
            credentials ?? new FakeCredentialStore(),
            NullLogger<MusicService>.Instance,
            cacheTtl ?? TimeSpan.FromMinutes(5));
        return new MusicTestHost(service, audio, handler);
    }

    /// <summary>轮询等待条件成立。</summary>
    /// <param name="condition">条件。</param>
    /// <param name="timeout">超时；默认 5 秒。</param>
    /// <returns>等待任务。</returns>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件未在超时时间内满足。");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>按 BV 统计请求次数。</summary>
    /// <param name="handler">假 HTTP 传输。</param>
    /// <param name="bvid">BV 号。</param>
    /// <returns>带该 bvid 查询参数的请求数。</returns>
    public static int RequestCountFor(FakeBiliHandler handler, string bvid)
        => handler.Requests.Count(request =>
            request.Uri.Query.Contains("bvid=" + bvid, StringComparison.Ordinal));
}
