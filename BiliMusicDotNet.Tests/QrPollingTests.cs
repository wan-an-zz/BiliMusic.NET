using System.Linq;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 轮询环节：状态映射、终止条件与失败容忍。
/// </summary>
/// <remarks>
/// 这些用例覆盖 <c>docs/bili_api/bilibili-api-reference.md</c> 里"扫码轮询"一节最容易踩的两类问题：
/// 读错层（外层 code 恒为 0）与缺终止出口（失效/取消/兜底超时）。
/// </remarks>
public sealed class QrPollingTests
{
    [Fact]
    public async Task 状态查询_读内层状态码而不是外层()
    {
        var handler = new FakeBiliHandler(url => url.Contains("generate")
            ? Responses.Json(Responses.GenerateBody)
            : Responses.Json(Responses.PollBody(Responses.StatusNotScanned)));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.PollQrStatusAsync(session, TestContext.Current.CancellationToken);

        // 外层 code 是 0（接口调用成功），若按外层判断会被误判成"登录成功"。
        Assert.Equal(LoginState.WaitingForScan, result.State);
        Assert.Equal(Responses.StatusNotScanned, result.RawStatus);
        Assert.Null(result.Credentials);
        Assert.Contains("扫描", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 状态查询_已扫码待确认()
    {
        var handler = new FakeBiliHandler(url => url.Contains("generate")
            ? Responses.Json(Responses.GenerateBody)
            : Responses.Json(Responses.PollBody(Responses.StatusNotConfirmed)));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.PollQrStatusAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(LoginState.WaitingForConfirm, result.State);
        Assert.Equal(LoginState.WaitingForConfirm, service.State);
    }

    [Fact]
    public async Task 状态查询_未知状态码不误判且原样保留()
    {
        var handler = new FakeBiliHandler(url => url.Contains("generate")
            ? Responses.Json(Responses.GenerateBody)
            : Responses.Json(Responses.PollBody(99999)));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.PollQrStatusAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(LoginState.WaitingForScan, result.State);
        Assert.Equal(99999, result.RawStatus);
        Assert.Contains("99999", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 状态查询_轮询请求带passport域Referer()
    {
        var handler = FakeBiliHandler.Waiting();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        await service.PollQrStatusAsync(session, TestContext.Current.CancellationToken);

        var pollRequest = handler.Requests.Single(r => r.Uri.AbsoluteUri.Contains("/qrcode/poll"));
        Assert.Equal("https://passport.bilibili.com/login", pollRequest.Referer);
        Assert.Contains($"qrcode_key={Responses.QrKey}", pollRequest.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 终止_二维码失效立即停止轮询()
    {
        var handler = new FakeBiliHandler(url => url.Contains("generate")
            ? Responses.Json(Responses.GenerateBody)
            : Responses.Json(Responses.PollBody(Responses.StatusExpired)));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.Expired, result.Outcome);
        Assert.Equal(LoginState.Expired, service.State);
        Assert.Equal(1, handler.PollCount);
    }

    [Fact]
    public async Task 终止_调用方取消时立即退出且不吞掉取消()
    {
        var handler = FakeBiliHandler.Waiting();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.WaitForConfirmationAsync(session, null, cts.Token));
    }

    [Fact]
    public async Task 终止_有效期耗尽时兜底超时()
    {
        var handler = FakeBiliHandler.Waiting();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        // 用一个已过期的会话驱动超时分支：等价于"一次 86038 都没收到"的兜底出口。
        var expiredSession = new QrSession(
            Responses.QrKey, Responses.QrUrl, DateTimeOffset.Now.AddSeconds(-1));

        var result = await service.WaitForConfirmationAsync(expiredSession, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.TimedOut, result.Outcome);
        Assert.Equal(LoginState.TimedOut, service.State);
        Assert.Equal(0, handler.PollCount);
    }

    [Fact]
    public async Task 终止_连续网络失败达到上限才中断()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.GenerateBody))
        {
            ThrowOnSend = new HttpRequestException("模拟断网"),
        };
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        // 取码也走同一个传输，所以先用一个不抛异常的取码结果。
        var session = new QrSession(
            Responses.QrKey, Responses.QrUrl, DateTimeOffset.Now.AddMinutes(3));

        var result = await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.NetworkFailed, result.Outcome);
        Assert.Equal(LoginState.Failed, service.State);
        Assert.Equal(5, handler.PollCount); // 少量抖动应重试，不是一次失败就放弃
    }

    [Fact]
    public async Task 终止_重复轮询同一二维码不重复触发登录事件()
    {
        var handler = FakeBiliHandler.Succeeding();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var loginCount = 0;
        service.LoginSucceeded += (_, _) => loginCount++;

        var first = await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);
        var second = await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.Succeeded, first.Outcome);
        Assert.Equal(QrLoginOutcome.Succeeded, second.Outcome);
        Assert.Equal(1, loginCount);
    }

    [Fact]
    public async Task 进度回调_每次状态变化都上报()
    {
        var handler = FakeBiliHandler.Succeeding();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var reports = new List<QrStatusResult>();
        await service.WaitForConfirmationAsync(session, new SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.NotEmpty(reports);
        Assert.Equal(LoginState.LoggedIn, reports[^1].State);
    }

    /// <summary>同步的 <see cref="IProgress{T}"/>，避免回调被 post 到线程池后断言时机不确定。</summary>
    /// <param name="sink">接收上报结果的集合。</param>
    private sealed class SyncProgress(List<QrStatusResult> sink)
        : IProgress<QrStatusResult>
    {
        public void Report(QrStatusResult value) => sink.Add(value);
    }
}
