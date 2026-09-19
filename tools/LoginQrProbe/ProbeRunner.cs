using System.Diagnostics;
using System.Net;
using System.Text;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tools.LoginQrProbe;

/// <summary>
/// 真机扫码验证探针：走一遍真实的取码 → 扫码 → 轮询 → 取用户信息 → 下载头像，
/// 并把每一步的实测结果与断言打印出来，最后产出脱敏报告。
/// </summary>
/// <remarks>
/// 单元测试只证明"按假响应能跑对"，本探针回答的是另一个问题：真实接口是否与假设一致。
/// 首次真机联调时重点看：二维码内容是否合法、轮询是否只在 302/200 两种形态下拿到凭据、Cookie 域是否让 nav 返回 code 0。
/// </remarks>
internal static class ProbeRunner
{
    /// <summary>
    /// 执行一次真机验证。
    /// </summary>
    /// <param name="options">命令行选项。</param>
    /// <param name="cancellationToken">取消令牌（Ctrl+C）。</param>
    /// <returns>进程退出码：0 全部通过，1 有断言失败，2 流程中断。</returns>
    public static async Task<int> RunAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine("== BiliMusic.NET 扫码登录真机验证 ==");
        Console.WriteLine($"   超时：{options.Timeout.TotalMinutes:0.#} 分钟    渲染：{(options.Render ? "开" : "关")}");
        Console.WriteLine();

        var result = new ProbeResult();
        var checks = result.Checks;
        ILogger<BiliLoginService> logger = options.Verbose
            ? new ConsoleLogger()
            : NullLogger<BiliLoginService>.Instance;

        using var handler = new HttpClientHandler
        {
            UseCookies = true,
            // 登录成功的 302 必须能观察到，否则拿不到 Set-Cookie。
            AllowAutoRedirect = false,
        };

        if (options.Proxy is not null)
        {
            handler.Proxy = new WebProxy(options.Proxy);
            handler.UseProxy = true;
            Console.WriteLine($"   代理：{options.Proxy}");
        }
        else
        {
            Console.WriteLine("   代理：使用系统代理设置（Windows 上会自动读取 Internet 选项）");
        }

        using var service = new BiliLoginService(handler, logger);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        var token = timeoutSource.Token;

        var progress = new Progress<QrStatusResult>(status =>
            Console.WriteLine($"   [{DateTime.Now:HH:mm:ss}] {Describe(status.State)}  {status.Message}"));

        service.StateChanged += (_, e) =>
        {
            if (e.State is LoginState.LoggedIn)
            {
                Console.WriteLine($"   [{DateTime.Now:HH:mm:ss}] 状态 → {e.State}");
            }
        };

        // ---- 第 1 步：取码 ----
        Console.WriteLine("[1/5] 取码…");
        QrSession session;
        try
        {
            session = await service.CreateQrSessionAsync(token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("   排查建议：");
            Console.WriteLine("     - 网络不可达：确认能访问 passport.bilibili.com，必要时加 --proxy http://127.0.0.1:7897");
            Console.WriteLine("     - 返回 -412：请求被风控拦截，稍后重试或更换网络出口");
            Console.WriteLine("     - 返回非 JSON：被登录门户/代理劫持，检查网络环境");
            return 2;
        }

        Console.WriteLine($"   二维码内容：{session.Content}");
        Console.WriteLine($"   失效时刻：{session.ExpiresAt:HH:mm:ss}（约 {(session.ExpiresAt - DateTimeOffset.Now).TotalMinutes:0.#} 分钟后）");

        var validContent = ProbeAssertions.IsValidQrContent(session.Content);
        var validLifetime = ProbeAssertions.HasReasonableLifetime(session);
        checks.Add((validContent, "二维码内容是合法的 bilibili https 登录地址"));
        checks.Add((validLifetime, "二维码有效期落在 2~5 分钟区间"));
        Console.WriteLine($"   {(validContent ? "PASS" : "FAIL")}  内容格式    {(validLifetime ? "PASS" : "FAIL")}  有效期区间");
        Console.WriteLine();

        // ---- 第 2 步：渲染二维码 ----
        Console.WriteLine("[2/5] 渲染二维码");
        if (options.Render)
        {
            RenderQr(session.Content);
            Console.WriteLine("   请用哔哩哔哩手机客户端扫描上方二维码，并在手机上确认登录。");
        }
        else
        {
            Console.WriteLine("   已跳过（未指定 --render）。可用下面的内容自行生成二维码：");
            Console.WriteLine($"   {session.Content}");
        }

        Console.WriteLine();

        // ---- 第 3 步：轮询 ----
        Console.WriteLine("[3/5] 等待扫码（Ctrl+C 可随时中止）…");
        var stopwatch = Stopwatch.StartNew();
        QrLoginResult login;
        try
        {
            login = await service.WaitForConfirmationAsync(session, progress, token);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Console.WriteLine();
            Console.WriteLine(cancellationToken.IsCancellationRequested
                ? "   已中止：收到 Ctrl+C。"
                : $"   已中断：等待超过 {options.Timeout.TotalMinutes:0.#} 分钟。");
            Console.WriteLine("   注意：轮询已停止，不会继续空转。");
            return 2;
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"   终局：{login.Outcome}（耗时 {stopwatch.Elapsed.TotalSeconds:0.#} 秒）");
        checks.Add((login.Outcome == QrLoginOutcome.Succeeded, $"轮询终局为 Succeeded（实际 {login.Outcome}）"));

        if (login.Outcome != QrLoginOutcome.Succeeded)
        {
            Console.WriteLine($"   {login.Message}");
            Console.WriteLine();
            Console.WriteLine("   这不是探针的问题，是本次登录没走完：");
            Console.WriteLine("     - Expired：二维码超时未扫，重跑本命令即可");
            Console.WriteLine("     - NetworkFailed：网络抖动导致连续失败，检查网络后重试");
            return 1;
        }

        var credentials = login.Credentials;
        var complete = credentials?.IsComplete == true;
        checks.Add((complete, "凭据三件套（SESSDATA + bili_jct + DedeUserID）齐备"));
        Console.WriteLine($"   {(complete ? "PASS" : "FAIL")}  凭据齐备");
        if (credentials is not null)
        {
            // 只打印长度与首尾，绝不打印完整凭据。
            Console.WriteLine($"   SESSDATA 长度 {credentials.SessData.Length}，bili_jct 长度 {credentials.BiliJct.Length}，DedeUserID={credentials.DedeUserId}");
        }

        Console.WriteLine();

        // ---- 第 4 步：用户信息 ----
        Console.WriteLine("[4/5] 用户信息（nav）");
        var profile = login.Profile;
        var gotProfile = profile is not null;
        checks.Add((gotProfile, "nav 返回用户昵称（Cookie 域正确）"));

        if (profile is null)
        {
            Console.WriteLine("   FAIL  未取到用户信息");
            Console.WriteLine("   排查建议：若凭据齐备却拿不到 nav，通常是 Cookie 域不匹配（应为 bilibili.com 而非 passport.bilibili.com）。");
        }
        else
        {
            Console.WriteLine($"   PASS  mid={profile.Mid}  昵称={profile.Nickname}");
            Console.WriteLine($"         大会员={(profile.IsVip ? "是" : "否")}  等级={profile.Level}");
            Console.WriteLine($"         头像={profile.AvatarUri}");
        }

        Console.WriteLine();

        // ---- 第 5 步：头像字节 ----
        Console.WriteLine("[5/5] 下载头像");
        var avatarBytes = 0;
        if (profile?.AvatarUri is { } avatarUri)
        {
            var bytes = await service.GetAvatarImageAsync(avatarUri, token);
            avatarBytes = bytes?.Length ?? 0;
            var ok = avatarBytes > 0;
            checks.Add((ok, "头像下载到非空字节（CDN Referer 正确）"));
            Console.WriteLine(ok
                ? $"   PASS  {avatarBytes} 字节"
                : "   FAIL  下载失败（403 通常是缺 Referer；也可能是地址过期）");
        }
        else
        {
            checks.Add((false, "存在头像地址可供下载"));
            Console.WriteLine("   FAIL  没有头像地址（上一步未取到用户信息）");
        }

        // ---- 主动登出，顺带清掉本进程持有的 Cookie ----
        service.Logout();
        Console.WriteLine();
        Console.WriteLine("   已执行 Logout()（仅清理本进程内的登录态，不影响其它设备）");

        Console.WriteLine();

        // ---- 报告 ----
        var final = new ProbeResult
        {
            QrContent = session.Content,
            QrExpiresAt = session.ExpiresAt,
            PollCount = 0,
            Credentials = credentials,
            Profile = profile,
            AvatarBytes = avatarBytes,
        };
        final.Checks.AddRange(checks);

        var passed = checks.Count(c => c.Passed);
        Console.WriteLine($"== 结果：{passed}/{checks.Count} 项通过 ==");
        foreach (var (ok, description) in checks.Where(c => !c.Passed))
        {
            Console.WriteLine($"   FAIL  {description}");
        }

        if (options.OutputFile is { } output)
        {
            await File.WriteAllTextAsync(output, final.ToMarkdown(), Encoding.UTF8, CancellationToken.None);
            Console.WriteLine($"   报告已写入：{Path.GetFullPath(output)}（凭据已脱敏）");
        }

        return passed == checks.Count ? 0 : 1;
    }

    /// <summary>把二维码内容渲染到控制台。</summary>
    /// <param name="content">二维码内容。</param>
    private static void RenderQr(string content)
    {
        Console.WriteLine();
        try
        {
            QrConsoleRenderer.WriteToConsole(content, new QrConsoleRenderer.Options(QuietZone: true));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 渲染失败不该影响验证本身：内容仍然有效，可自行生成二维码。
            Console.WriteLine($"   渲染失败：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("   请用 --no-render 取内容后自行生成二维码（登录链路不受影响）。");
        }

        Console.WriteLine();
    }

    /// <summary>状态的中文短标签。</summary>
    private static string Describe(LoginState state) => state switch
    {
        LoginState.WaitingForScan => "待扫码",
        LoginState.WaitingForConfirm => "待确认",
        LoginState.LoggedIn => "已登录",
        LoginState.Expired => "已失效",
        LoginState.TimedOut => "已超时",
        LoginState.Failed => "已失败",
        _ => state.ToString(),
    };
}

/// <summary>命令行选项。</summary>
internal sealed class ProbeOptions
{
    /// <summary>是否只跑离线自检（不联网）。</summary>
    public bool Offline { get; init; }

    /// <summary>整个验证流程的超时。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>是否在终端渲染二维码。</summary>
    public bool Render { get; init; } = true;

    /// <summary>显式代理地址；为空则用系统代理。</summary>
    public Uri? Proxy { get; init; }

    /// <summary>报告输出路径。</summary>
    public string? OutputFile { get; init; }

    /// <summary>是否输出服务内部日志（排查用）。</summary>
    public bool Verbose { get; init; }
}

/// <summary>把服务日志打到控制台的最小日志器（<c>--verbose</c> 时启用）。</summary>
internal sealed class ConsoleLogger : ILogger<BiliLoginService>
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    /// <inheritdoc />
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

        // 服务本身已保证不把凭据写进日志，这里直接转发即可。
        Console.WriteLine($"   [log:{logLevel}] {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.WriteLine($"            {exception.GetType().Name}: {exception.Message}");
        }
    }
}
