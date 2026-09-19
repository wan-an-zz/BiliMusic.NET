using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tools.LoginQrProbe;

/// <summary>
/// <c>--offline</c> 自检：用假传输把探针自身的编排与断言逻辑跑一遍。
/// </summary>
/// <remarks>
/// 真机扫码依赖网络与真人操作，无法在 CI 或离线环境重复执行。
/// 这一模式保证"探针代码本身是好的"——真机跑失败时，问题一定在环境或接口，而不是探针。
/// </remarks>
internal static class OfflineSelfCheck
{
    private static int _failed;

    /// <summary>执行全部自检场景。</summary>
    /// <returns>失败场景数。</returns>
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("== 离线自检：验证探针自身的编排与断言逻辑 ==");
        Console.WriteLine("（不访问网络，用假传输模拟 B 站响应）");
        Console.WriteLine();

        await ScenarioSuccessAsync();
        await ScenarioInvalidQrContentAsync();
        await ScenarioExpiredAsync();
        await ScenarioWrongInnerCodeAsync();
        await ScenarioProfileFailedAsync();
        await ScenarioAvatarForbiddenAsync();
        await ScenarioRestoreInvalidAsync();

        Console.WriteLine();
        Console.WriteLine(_failed == 0
            ? "== 自检通过：探针逻辑可用，可执行真机扫码验证 =="
            : $"== 自检失败：{_failed} 个场景未通过，请先修探针 ==");

        return _failed;
    }

    private static async Task ScenarioSuccessAsync()
    {
        await RunScenarioAsync("完整成功链路（取码 → 扫码 → 凭据 → 昵称头像 → 头像字节）", async reporter =>
        {
            var handler = FakeBiliHandler.Succeeding();
            using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, CreateCredentials());

            var session = await service.CreateQrSessionAsync();
            reporter.Check(ProbeAssertions.IsValidQrContent(session.Content), "二维码内容是合法 https URL");
            reporter.Check(ProbeAssertions.HasReasonableLifetime(session), "有效期约 3 分钟");

            probeRender(session.Content);

            var result = await service.WaitForConfirmationAsync(session);
            reporter.Check(result.Outcome == QrLoginOutcome.Succeeded, "终局为 Succeeded");
            reporter.Check(result.Credentials?.IsComplete == true, "凭据三件套齐备");
            reporter.Check(result.Profile?.Nickname == "测试用户", "取到昵称");
            reporter.Check(result.Profile?.AvatarUri is not null, "取到头像地址");

            var bytes = await service.GetAvatarImageAsync(result.Profile!.AvatarUri);
            reporter.Check(bytes is { Length: > 0 }, "头像字节非空");

            var report = new ProbeResult
            {
                QrContent = session.Content,
                QrExpiresAt = session.ExpiresAt,
                PollCount = handler.PollCount,
                Credentials = result.Credentials,
                Profile = result.Profile,
                AvatarBytes = bytes?.Length ?? 0,
            };

            var markdown = report.ToMarkdown();
            reporter.Check(markdown.Contains("已脱敏", StringComparison.Ordinal), "报告声明了凭据已脱敏");
            reporter.Check(
                result.Credentials is null || !markdown.Contains(result.Credentials.SessData, StringComparison.Ordinal),
                "报告正文不含 SESSDATA 原值");
            reporter.Check(
                result.Credentials is null || !markdown.Contains(result.Credentials.BiliJct, StringComparison.Ordinal),
                "报告正文不含 bili_jct 原值");

            return report;
        });
    }

    private static async Task ScenarioInvalidQrContentAsync()
    {
        await RunScenarioAsync("二维码内容不合法时自检必须失败", async reporter =>
        {
            var handler = new FakeBiliHandler(_ => Responses.Json(
                """{"code":0,"message":"0","ttl":1,"data":{"url":"not-a-url","qrcode_key":"k"}}"""));
            using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, CreateCredentials());

            var session = await service.CreateQrSessionAsync();
            reporter.Check(!ProbeAssertions.IsValidQrContent(session.Content), "识别出非法内容（负向断言）");

            await Task.CompletedTask;
            return new ProbeResult { QrContent = session.Content };
        });
    }

    private static async Task ScenarioExpiredAsync()
    {
        await RunScenarioAsync("二维码失效（86038）", async reporter =>
        {
            var handler = new FakeBiliHandler(url => url.Contains("generate")
                ? Responses.Json(Responses.GenerateBody)
                : Responses.Json(Responses.PollBody(Responses.StatusExpired)));
            using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, CreateCredentials());

            var session = await service.CreateQrSessionAsync();
            var result = await service.WaitForConfirmationAsync(session);

            reporter.Check(result.Outcome == QrLoginOutcome.Expired, "终局为 Expired");
            reporter.Check(handler.PollCount == 1, "失效后立即停止轮询");
            return new ProbeResult { QrContent = session.Content, PollCount = handler.PollCount };
        });
    }

    private static async Task ScenarioWrongInnerCodeAsync()
    {
        await RunScenarioAsync("只读外层 code 会误判成功（守内层 data.code 这条）", async reporter =>
        {
            var handler = new FakeBiliHandler(url => url.Contains("generate")
                ? Responses.Json(Responses.GenerateBody)
                : Responses.Json(Responses.PollBody(Responses.StatusNotScanned)));
            using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, CreateCredentials());

            var session = await service.CreateQrSessionAsync();
            var status = await service.PollQrStatusAsync(session);

            reporter.Check(status.State == LoginState.WaitingForScan, "未扫码状态映射正确");
            reporter.Check(status.RawStatus == Responses.StatusNotScanned, "原始状态码为 86101");
            reporter.Check(status.Credentials is null, "未扫码时没有凭据");
            return new ProbeResult { QrContent = session.Content, PollCount = handler.PollCount };
        });
    }

    private static async Task ScenarioProfileFailedAsync()
    {
        await RunScenarioAsync("资料接口失败不影响登录成立", async reporter =>
        {
            var handler = new FakeBiliHandler(url => url.Contains("generate")
                ? Responses.Json(Responses.GenerateBody)
                : url.Contains("/qrcode/poll")
                    ? Responses.Redirect(Responses.SetCookies)
                    : throw new HttpRequestException("模拟资料接口不可用"));
            using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, CreateCredentials());

            var session = await service.CreateQrSessionAsync();
            var result = await service.WaitForConfirmationAsync(session);

            reporter.Check(result.Outcome == QrLoginOutcome.Succeeded, "登录仍然成功");
            reporter.Check(result.Profile is null, "资料为空但不谎报失败");
            return new ProbeResult { QrContent = session.Content, Credentials = result.Credentials };
        });
    }

    private static async Task ScenarioAvatarForbiddenAsync()
    {
        await RunScenarioAsync("头像 403 时返回空而不抛异常", async reporter =>
        {
            var handler = new FakeBiliHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
            using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, CreateCredentials());

            var bytes = await service.GetAvatarImageAsync(new Uri("https://i0.hdslb.com/bfs/face/demo.jpg"));

            reporter.Check(bytes is null, "403 时返回空");
            return new ProbeResult { QrContent = "(不需要)" };
        });
    }

    private static async Task ScenarioRestoreInvalidAsync()
    {
        await RunScenarioAsync("失效凭据恢复登录态必须返回 false", async reporter =>
        {
            var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavNotLoggedIn));
            using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, CreateCredentials());

            var credentials = new BiliLoginCredentials(
                "expired", "expired", "123456", "SESSDATA=expired; bili_jct=expired; DedeUserID=123456");

            var restored = await service.TryRestoreSessionAsync(credentials);

            reporter.Check(!restored, "失效凭据返回 false");
            reporter.Check(!service.IsLoggedIn, "不残留登录态");
            return new ProbeResult { QrContent = "(不需要)" };
        });
    }

    /// <summary>
    /// 自检专用的凭据服务：路径落在临时目录，避免把测试凭据写进真实用户目录。
    /// </summary>
    /// <returns>凭据服务。</returns>
    private static CredentialService CreateCredentials()
        => new(Path.Combine(Path.GetTempPath(), "BiliMusicDotNet.LoginQrProbe"), NullLogger<CredentialService>.Instance);

    /// <summary>跑一个场景，捕获异常并统计失败数。</summary>
    private static async Task RunScenarioAsync(string name, Func<Reporter, Task<ProbeResult>> body)
    {
        Console.WriteLine($"-- {name}");
        var reporter = new Reporter();

        try
        {
            await body(reporter);
        }
        catch (Exception ex)
        {
            reporter.Check(false, $"场景抛出异常：{ex.GetType().Name}: {ex.Message}");
        }

        if (reporter.Failed > 0)
        {
            _failed++;
        }

        Console.WriteLine();
    }

    /// <summary>断言记录器（离线自检用）。</summary>
    private sealed class Reporter
    {
        /// <summary>失败断言数。</summary>
        public int Failed { get; private set; }

        /// <summary>记录一条断言。</summary>
        /// <param name="condition">是否为真。</param>
        /// <param name="description">断言描述。</param>
        public void Check(bool condition, string description)
        {
            if (condition)
            {
                Console.WriteLine($"   PASS  {description}");
            }
            else
            {
                Failed++;
                Console.WriteLine($"   FAIL  {description}");
            }
        }
    }

    /// <summary>确认二维码生成与渲染管线可跑通（用真实长度的内容）。</summary>
    /// <param name="content">二维码内容。</param>
    private static void probeRender(string content)
    {
        var lines = QrConsoleRenderer.Render(content, new QrConsoleRenderer.Options(QuietZone: true));

        // 只报告尺寸，不打印整张图，避免刷屏。
        var width = lines.Count > 0 ? lines[0].Length : 0;
        Console.WriteLine($"   INFO  二维码渲染管线可用（{lines.Count} 行 × {width} 字符）");
    }
}
