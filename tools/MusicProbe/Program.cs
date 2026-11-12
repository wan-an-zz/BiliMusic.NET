using System.Globalization;
using BiliMusicDotNet.Tests.Support;

namespace BiliMusicDotNet.Tools.MusicProbe;

/// <summary>入口：解析命令行后进入真机播放验证或离线自检。</summary>
internal static class Program
{
    /// <summary>程序入口。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码。</returns>
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintUsage();
            return 0;
        }

        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"参数错误：{error}");
            Console.Error.WriteLine();
            PrintUsage();
            return 64;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("收到 Ctrl+C，正在停止…");
            cancellation.Cancel();
        };

        return await ProbeRunner.RunAsync(options, cancellation.Token);
    }

    private static bool TryParse(string[] args, out ProbeOptions options, out string? error)
    {
        var offline = false;
        var diagnose = false;
        var verbose = false;
        var waitEnd = false;
        var seconds = 6;
        var timeout = TimeSpan.FromSeconds(120);
        var bvs = new List<string>();
        Uri? proxy = null;
        Uri? url = null;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--offline":
                    offline = true;
                    break;

                case "--diagnose":
                    diagnose = true;
                    break;

                case "--verbose":
                    verbose = true;
                    break;

                case "--wait-end":
                    waitEnd = true;
                    break;

                case "--bv":
                    if (!TryNext(args, ref i, out var bvid, out error))
                    {
                        options = new ProbeOptions();
                        return false;
                    }

                    bvs.Add(bvid);
                    break;

                case "--seconds":
                    if (!TryNext(args, ref i, out var secondsText, out error) ||
                        !int.TryParse(secondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) ||
                        seconds <= 0)
                    {
                        error ??= "--seconds 需要一个正整数";
                        options = new ProbeOptions();
                        return false;
                    }

                    break;

                case "--timeout":
                    if (!TryNext(args, ref i, out var timeoutText, out error) ||
                        !double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture, out var timeoutSeconds) ||
                        timeoutSeconds <= 0)
                    {
                        error ??= "--timeout 需要一个正数（单位：秒）";
                        options = new ProbeOptions();
                        return false;
                    }

                    timeout = TimeSpan.FromSeconds(timeoutSeconds);
                    break;

                case "--url":
                    if (!TryNext(args, ref i, out var urlText, out error) ||
                        !Uri.TryCreate(urlText, UriKind.Absolute, out url))
                    {
                        error ??= $"--url 需要一个绝对地址（收到 {urlText}）";
                        options = new ProbeOptions();
                        return false;
                    }

                    break;

                case "--proxy":
                    if (!TryNext(args, ref i, out var proxyText, out error) ||
                        !Uri.TryCreate(proxyText, UriKind.Absolute, out proxy))
                    {
                        error ??= $"--proxy 需要一个绝对地址，例如 http://127.0.0.1:7897（收到 {proxyText}）";
                        options = new ProbeOptions();
                        return false;
                    }

                    break;

                default:
                    error = $"未知参数：{args[i]}";
                    options = new ProbeOptions();
                    return false;
            }
        }

        options = new ProbeOptions
        {
            Offline = offline,
            Diagnose = diagnose,
            Verbose = verbose,
            WaitEnd = waitEnd,
            Seconds = seconds,
            Timeout = timeout,
            Bvs = bvs.Count == 0
                ? [MusicResponses.Bv1, MusicResponses.Bv2]
                : bvs.ToArray(),
            Proxy = proxy,
            Url = url,
        };

        return true;

        static bool TryNext(string[] all, ref int index, out string value, out string? failure)
        {
            if (index + 1 >= all.Length)
            {
                value = string.Empty;
                failure = $"参数 {all[index]} 缺少取值";
                return false;
            }

            index++;
            value = all[index];
            failure = null;
            return true;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            BiliMusic.NET 音乐服务真机验证探针

            用法：
              dotnet run --project tools/MusicProbe                      # 真机播放（默认 BV1aJaz6yEgq、BV1cupJ6EEff）
              dotnet run --project tools/MusicProbe -- --offline         # 离线自检（不联网、不需要 bass.dll）
              dotnet run --project tools/MusicProbe -- --bv BV1... --bv BV1... --seconds 10

            选项：
              --offline          只跑离线自检：用假 HTTP 与假引擎验证编排
              --diagnose         只诊断 BASS 建流：尝试不同请求头与缓冲组合
              --bv <BV>          指定曲目，可重复；默认 BV1aJaz6yEgq、BV1cupJ6EEff
              --url <地址>       诊断模式直接使用该地址，跳过 B 站解析
              --seconds <秒>     每步观察播放进度的时长，默认 6
              --timeout <秒>     整个流程的超时，默认 120
              --proxy <地址>     显式代理，例如 http://127.0.0.1:7897
              --wait-end         等待当前曲目自然播完以验证自动切歌（可能耗时数分钟）
              --verbose          额外打印服务内部日志
              -h, --help         显示本帮助

            退出码：
              0  全部断言通过
              1  有断言失败
              2  基础设施失败（原生库/网络/声卡/取消）
              64 参数用法错误

            首次真机运行前先执行：
              pwsh -File tools/fetch-bass.ps1
            """);
    }
}

/// <summary>命令行选项。</summary>
internal sealed class ProbeOptions
{
    /// <summary>是否只跑离线自检。</summary>
    public bool Offline { get; init; }

    /// <summary>是否只诊断 BASS 建流。</summary>
    public bool Diagnose { get; init; }

    /// <summary>是否输出服务内部日志。</summary>
    public bool Verbose { get; init; }

    /// <summary>是否等待自然播完。</summary>
    public bool WaitEnd { get; init; }

    /// <summary>观察播放进度的秒数。</summary>
    public int Seconds { get; init; } = 6;

    /// <summary>整个流程的超时。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>要播放的 BV 列表。</summary>
    public IReadOnlyList<string> Bvs { get; init; } = [];

    /// <summary>显式代理地址。</summary>
    public Uri? Proxy { get; init; }

    /// <summary>诊断模式直接使用的地址。</summary>
    public Uri? Url { get; init; }
}
