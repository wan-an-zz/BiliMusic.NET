using System.Globalization;

namespace BiliMusicDotNet.Tools.LoginQrProbe;

/// <summary>
/// 入口：解析命令行后进入真机验证或离线自检。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 程序入口。
    /// </summary>
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
            return 64;   // EX_USAGE
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // 别让运行时直接杀进程：要把取消传到轮询里，验证"取消即退出"这条。
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("收到 Ctrl+C，正在停止轮询…");
            cancellation.Cancel();
        };

        return options.Offline
            ? await OfflineSelfCheck.RunAsync()
            : await ProbeRunner.RunAsync(options, cancellation.Token);
    }

    private static bool TryParse(string[] args, out ProbeOptions options, out string? error)
    {
        var offline = false;
        var render = true;
        var verbose = false;
        var timeout = TimeSpan.FromMinutes(5);
        Uri? proxy = null;
        string? output = null;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--offline":
                    offline = true;
                    break;

                case "--no-render":
                    render = false;
                    break;

                case "--verbose":
                    verbose = true;
                    break;

                case "--render":
                    render = true;
                    break;

                case "--timeout":
                    if (!TryNext(args, ref i, out var minutesText, out error) ||
                        !double.TryParse(minutesText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
                        minutes <= 0)
                    {
                        error ??= "--timeout 需要一个正数（单位：分钟）";
                        options = new ProbeOptions();
                        return false;
                    }

                    timeout = TimeSpan.FromMinutes(minutes);
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

                case "--output":
                    if (!TryNext(args, ref i, out output, out error))
                    {
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
            Timeout = timeout,
            Render = render,
            Proxy = proxy,
            OutputFile = output,
            Verbose = verbose,
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
            BiliMusic.NET 扫码登录真机验证探针

            用法：
              dotnet run --project tools/LoginQrProbe                # 真机扫码验证
              dotnet run --project tools/LoginQrProbe -- --offline   # 离线自检（不联网）

            选项：
              --offline          只跑离线自检：用假响应验证探针自身逻辑
              --no-render        不渲染二维码，只打印二维码内容（终端显示异常时用）
              --timeout <分钟>   整个流程的超时，默认 5
              --proxy <地址>     显式代理，例如 http://127.0.0.1:7897；默认跟随系统代理
              --output <路径>    把脱敏报告写入文件
              --verbose          额外打印服务内部日志（排查用）
              -h, --help         显示本帮助

            退出码：
              0  全部断言通过
              1  登录完成但有断言失败
              2  取码失败 / 超时 / 被取消
              64 参数用法错误

            注意：本探针会把二维码内容打印到终端，扫码期间请勿录屏或共享屏幕。
            """);
    }
}
