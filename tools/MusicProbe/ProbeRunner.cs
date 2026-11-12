using System.Net;
using System.Net.Http;
using BiliMusicDotNet.Models.Music;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tools.MusicProbe;

/// <summary>
/// 真机播放验证：真实 B 站地址 + BASS 网络流，逐步断言播放、暂停、切歌、替换与删除。
/// </summary>
internal static class ProbeRunner
{
    /// <summary>执行一次验证。</summary>
    /// <param name="options">命令行选项。</param>
    /// <param name="outerCancellation">外部取消（Ctrl+C）。</param>
    /// <returns>进程退出码。</returns>
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken outerCancellation)
    {
        Console.WriteLine("== BiliMusic.NET 音乐服务验证 ==");
        Console.WriteLine(options.Offline
            ? "   模式：离线自检（假 HTTP + 假音频引擎）"
            : $"   模式：真机播放    曲目：{string.Join(", ", options.Bvs)}");
        Console.WriteLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        timeout.CancelAfter(options.Timeout);
        var ct = timeout.Token;
        var report = new ProbeReport();

        try
        {
            if (options.Diagnose)
            {
                return await Diagnose.RunAsync(options, ct);
            }

            if (options.Offline)
            {
                await RunOfflineAsync(options, report, ct);
            }
            else
            {
                var infrastructure = await RunRealAsync(options, report, ct);
                if (infrastructure != 0)
                {
                    return infrastructure;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine(outerCancellation.IsCancellationRequested
                ? "   已中止：收到 Ctrl+C。"
                : $"   已超时：超过 {options.Timeout.TotalSeconds:0} 秒。");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"   基础设施失败：{ex.GetType().Name}: {ex.Message}");
            if (options.Verbose)
            {
                Console.WriteLine(ex);
            }

            return 2;
        }

        return report.PrintSummary();
    }

    private static async Task<int> RunRealAsync(
        ProbeOptions options,
        ProbeReport report,
        CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "bass.dll")))
        {
            Console.WriteLine("缺少 bass.dll。先执行：pwsh -File tools/fetch-bass.ps1");
            return 2;
        }

        ILogger<MusicService> logger = options.Verbose
            ? new ConsoleLogger<MusicService>()
            : NullLogger<MusicService>.Instance;
        var engine = new BassAudioEngine(FormatBassProxy(options.Proxy), logger);
        using var service = new MusicService(
            CreateHandler(options.Proxy),
            engine,
            new FakeCredentialStore(),
            logger,
            TimeSpan.FromMinutes(5));

        MusicPlaybackFailedEventArgs? lastFailure = null;
        service.PlaybackFailed += (_, e) =>
        {
            lastFailure = e;
            Console.WriteLine($"   [播放失败] {e.Bvid}: {e.Message}");
        };

        Console.WriteLine("[1/6] 载入播放列表并开始播放");
        await service.InsertMusicsAsync(options.Bvs, cancellationToken: ct);
        report.Check(
            service.Playlist.Select(track => track.Bvid).SequenceEqual(options.Bvs),
            "播放列表顺序与输入一致");
        report.Check(
            service.Current?.Bvid == options.Bvs[0],
            "自动播放列表第一首");

        if (service.Current is null || service.State == MusicPlaybackState.Failed)
        {
            Console.WriteLine($"   加载失败：{lastFailure?.Message ?? "未知原因"}");
            Console.WriteLine("   排查：确认能访问 api.bilibili.com 与音频 CDN；--verbose 查看日志；--proxy 指定代理。");
            return 2;
        }

        report.Check(service.State == MusicPlaybackState.Playing, "状态进入 Playing");
        PrintCurrent(service);

        Console.WriteLine($"[2/6] 观察播放进度（最多 {options.Seconds} 秒）");
        var start = service.Position;
        var advanced = await WaitUntilAsync(
            () => service.Position > start,
            TimeSpan.FromSeconds(options.Seconds),
            ct);
        report.Check(advanced, "播放位置在推进");
        PrintCurrent(service);

        Console.WriteLine("[3/6] 下一首 / 上一首");
        if (options.Bvs.Count > 1)
        {
            report.Check(await service.PlayNextAsync(ct), "PlayNext 成功");
            report.Check(service.Current?.Bvid == options.Bvs[1], "当前为第二首");
            report.Check(await service.PlayPreviousAsync(ct), "PlayPrevious 成功");
            report.Check(service.Current?.Bvid == options.Bvs[0], "当前回到第一首");
        }
        else
        {
            Console.WriteLine("   跳过：只有一首曲目");
        }

        Console.WriteLine("[4/6] 暂停 / 继续");
        report.Check(await service.PauseAsync(ct), "Pause 成功");
        report.Check(service.State == MusicPlaybackState.Paused, "状态为 Paused");
        report.Check(await service.ResumeAsync(ct), "Resume 成功");
        report.Check(service.State == MusicPlaybackState.Playing, "状态回到 Playing");

        Console.WriteLine("[5/6] 播放列表内指定曲目");
        if (options.Bvs.Count > 1)
        {
            report.Check(
                await service.PlayPlaylistMusicAsync(options.Bvs[1], ct),
                "PlayPlaylistMusic 下一首分支成功");
            report.Check(service.Current?.Bvid == options.Bvs[1], "当前为选中曲目");
            report.Check(
                service.Playlist.Count > 0 && service.Playlist[0].Bvid == options.Bvs[1],
                "选中曲目已移到第一位");
            report.Check(
                service.Playlist.Count < 2 || service.Playlist[1].Bvid == options.Bvs[0],
                "原当前曲目紧随其后");

            if (options.Bvs.Count > 2)
            {
                var nonNext = options.Bvs[2];
                report.Check(
                    await service.PlayPlaylistMusicAsync(nonNext, ct),
                    "PlayPlaylistMusic 非下一首分支成功");
                report.Check(service.Current?.Bvid == nonNext, "非下一首分支当前曲目正确");
                report.Check(service.Playlist[0].Bvid == nonNext, "非下一首分支已移到第一位");
            }
            else
            {
                Console.WriteLine("   非下一首分支需要至少 3 首曲目，已跳过");
            }
        }

        Console.WriteLine("[6/6] 删除 / 整表替换");
        var removable = service.Playlist.FirstOrDefault(track => track.Bvid != service.Current?.Bvid);
        if (removable is not null)
        {
            await service.RemoveMusicsAsync([removable.Bvid], ct);
            report.Check(
                service.Playlist.All(track => track.Bvid != removable.Bvid),
                "删除非当前曲目成功");
        }

        if (service.Current is { } current)
        {
            var rejected = false;
            try
            {
                await service.RemoveMusicsAsync([current.Bvid], ct);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            report.Check(rejected, "删除当前曲目被拒绝");
        }

        await service.ReplacePlaylistAsync([options.Bvs[0]], ct);
        report.Check(service.Playlist.Count == 1, "整表替换后只剩新列表");
        report.Check(service.Current?.Bvid == options.Bvs[0], "整表替换后自动播放第一首");
        report.Check(service.State == MusicPlaybackState.Playing, "整表替换后状态为 Playing");

        if (options.WaitEnd)
        {
            Console.WriteLine("   等待当前曲目自然播完…");
            var before = service.Current?.Bvid;
            var switched = await WaitUntilAsync(
                () => service.Current?.Bvid != before || service.State == MusicPlaybackState.Stopped,
                options.Timeout,
                ct);
            report.Check(switched, "自然播完后切歌或停止");
        }

        await service.ReplacePlaylistAsync([], ct);
        report.Check(service.Playlist.Count == 0, "空列表替换后播放列表为空");
        report.Check(service.Current is null, "空列表替换后没有当前曲目");
        report.Check(service.State == MusicPlaybackState.Idle, "空列表替换后停在 Idle");

        return 0;
    }

    private static async Task RunOfflineAsync(
        ProbeOptions options,
        ProbeReport report,
        CancellationToken ct)
    {
        var audio = new FakeAudioEngine();
        var handler = new FakeBiliHandler(MusicResponses.Router());
        using var service = new MusicService(
            handler,
            audio,
            new FakeCredentialStore(),
            NullLogger<MusicService>.Instance,
            TimeSpan.FromMinutes(5));
        var failed = false;
        service.PlaybackFailed += (_, _) => failed = true;

        Console.WriteLine("[1/7] 载入两首并自动播放");
        await service.InsertMusicsAsync(
            [MusicResponses.Bv1, MusicResponses.Bv2],
            cancellationToken: ct);
        report.Check(service.Playlist.Count == 2, "列表载入两首");
        report.Check(service.Current?.Bvid == MusicResponses.Bv1, "自动播放第一首");
        report.Check(service.State == MusicPlaybackState.Playing, "状态为 Playing");
        report.Check(audio.Loads.Count == 1, "只为当前曲目建流");

        Console.WriteLine("[2/7] 模拟自然播完并自动切歌");
        audio.RaiseEnded();
        var switched = await WaitUntilAsync(
            () => service.Current?.Bvid == MusicResponses.Bv2
                  && service.State == MusicPlaybackState.Playing,
            TimeSpan.FromSeconds(5),
            ct);
        report.Check(switched, "自动切到下一首");
        report.Check(audio.Loads.Count == 2, "为下一首新建流");

        Console.WriteLine("[3/7] 上一首 / 暂停 / 继续");
        report.Check(await service.PlayPreviousAsync(ct), "PlayPrevious 成功");
        report.Check(service.Current?.Bvid == MusicResponses.Bv1, "回到第一首");
        report.Check(await service.PauseAsync(ct), "Pause 成功");
        report.Check(service.State == MusicPlaybackState.Paused, "状态为 Paused");
        report.Check(await service.ResumeAsync(ct), "Resume 成功");
        report.Check(service.State == MusicPlaybackState.Playing, "状态回到 Playing");

        Console.WriteLine("[4/7] 列表内指定曲目（下一首分支）");
        report.Check(
            await service.PlayPlaylistMusicAsync(MusicResponses.Bv2, ct),
            "PlayPlaylistMusic 成功");
        report.Check(service.Playlist[0].Bvid == MusicResponses.Bv2, "选中曲目移到第一位");
        report.Check(service.Current?.Bvid == MusicResponses.Bv2, "当前为选中曲目");

        Console.WriteLine("[5/7] 整表替换并预取新下一首");
        await service.ReplacePlaylistAsync(
            [MusicResponses.Bv3, MusicResponses.Bv4],
            ct);
        report.Check(service.Playlist.Count == 2, "替换后列表长度正确");
        report.Check(service.Current?.Bvid == MusicResponses.Bv3, "替换后自动播放第一首");
        report.Check(service.State == MusicPlaybackState.Playing, "替换后状态为 Playing");
        await WaitUntilAsync(
            () => handler.Requests.Count(request =>
                request.Uri.Query.Contains("bvid=" + MusicResponses.Bv4, StringComparison.Ordinal)) >= 2,
            TimeSpan.FromSeconds(5),
            ct);
        report.Check(
            handler.Requests.Count(request =>
                request.Uri.Query.Contains("bvid=" + MusicResponses.Bv4, StringComparison.Ordinal)) >= 2,
            "预取新列表的下一首");

        Console.WriteLine("[6/7] 删除保护");
        await service.RemoveMusicsAsync([MusicResponses.Bv4], ct);
        report.Check(
            service.Playlist.All(track => track.Bvid != MusicResponses.Bv4),
            "删除非当前曲目成功");

        var rejected = false;
        try
        {
            await service.RemoveMusicsAsync([MusicResponses.Bv3], ct);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        report.Check(rejected, "删除当前曲目被拒绝");

        Console.WriteLine("[7/7] 空列表替换");
        await service.ReplacePlaylistAsync([], ct);
        report.Check(service.Playlist.Count == 0, "播放列表已清空");
        report.Check(service.Current is null, "当前曲目为空");
        report.Check(service.State == MusicPlaybackState.Idle, "状态为 Idle");
        report.Check(!failed, "全程没有用户可见的播放失败");
    }

    private static HttpClientHandler CreateHandler(Uri? proxy)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli,
        };

        if (proxy is not null)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }

    private static string? FormatBassProxy(Uri? proxy)
    {
        if (proxy is null)
        {
            return null;
        }

        var userInfo = string.IsNullOrEmpty(proxy.UserInfo) ? string.Empty : proxy.UserInfo + "@";
        return userInfo + proxy.Host + ":" + proxy.Port;
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(100, ct);
        }

        return true;
    }

    private static void PrintCurrent(MusicService service)
    {
        var position = service.Position.ToString(@"mm\:ss");
        var duration = service.Duration.ToString(@"mm\:ss");
        Console.WriteLine(
            $"   {service.Current?.Title ?? "(未解析)"} / {service.Current?.OwnerName ?? "?"} " +
            $"/ {position} / {duration} / {service.State}");
    }
}

/// <summary>断言清单与退出码。</summary>
internal sealed class ProbeReport
{
    private readonly List<(bool Passed, string Description)> _checks = [];

    public void Check(bool passed, string description)
    {
        _checks.Add((passed, description));
        Console.WriteLine($"   {(passed ? "PASS" : "FAIL")}  {description}");
    }

    public int PrintSummary()
    {
        var passed = _checks.Count(check => check.Passed);
        Console.WriteLine();
        Console.WriteLine($"== 结果：{passed}/{_checks.Count} 项通过 ==");
        foreach (var (_, description) in _checks.Where(check => !check.Passed))
        {
            Console.WriteLine($"   FAIL  {description}");
        }

        return passed == _checks.Count ? 0 : 1;
    }
}

/// <summary>把服务日志打到控制台的最小日志器（--verbose 时启用）。</summary>
internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        Console.WriteLine($"   [log:{logLevel}] {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.WriteLine($"            {exception.GetType().Name}: {exception.Message}");
        }
    }
}
