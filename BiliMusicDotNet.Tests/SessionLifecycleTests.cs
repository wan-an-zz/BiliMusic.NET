using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 登录态恢复、登出与资源释放。
/// </summary>
public sealed class SessionLifecycleTests
{
    [Fact]
    public async Task 恢复登录态_凭据有效()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavLoggedIn));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var credentials = new IBiliLoginService.BiliLoginCredentials(
            "demo-sessdata-value",
            "demo-jct-value",
            "123456",
            "SESSDATA=demo-sessdata-value; bili_jct=demo-jct-value; DedeUserID=123456");

        var restored = await service.TryRestoreSessionAsync(credentials, TestContext.Current.CancellationToken);

        Assert.True(restored);
        Assert.True(service.IsLoggedIn);
        Assert.Equal(IBiliLoginService.LoginState.LoggedIn, service.State);
        Assert.Equal(123456, service.CurrentProfile?.Mid);
        Assert.Contains("bili_jct=demo-jct-value", handler.Requests[^1].Cookie ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 恢复登录态_凭据失效()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavNotLoggedIn));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var credentials = new IBiliLoginService.BiliLoginCredentials(
            "expired", "expired", "123456", "SESSDATA=expired; bili_jct=expired; DedeUserID=123456");

        var restored = await service.TryRestoreSessionAsync(credentials, TestContext.Current.CancellationToken);

        Assert.False(restored);
        Assert.False(service.IsLoggedIn, "恢复失败不应残留登录态");
    }

    [Fact]
    public async Task 恢复登录态_凭据不完整时直接拒绝()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavLoggedIn));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var credentials = new IBiliLoginService.BiliLoginCredentials("only-sessdata", string.Empty, string.Empty, string.Empty);

        var restored = await service.TryRestoreSessionAsync(credentials, TestContext.Current.CancellationToken);

        Assert.False(restored);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 登出_清空本地状态并触发事件()
    {
        var handler = FakeBiliHandler.Succeeding();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        IBiliLoginService.LoggedOutEventArgs? loggedOut = null;
        service.LoggedOut += (_, e) => loggedOut = e;

        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);
        await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(service.IsLoggedIn);

        service.Logout();

        Assert.False(service.IsLoggedIn);
        Assert.Null(service.CurrentProfile);
        Assert.Equal(IBiliLoginService.LoginState.Idle, service.State);
        Assert.NotNull(loggedOut);
    }

    [Fact]
    public async Task 释放_后续调用抛ObjectDisposedException()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.GenerateBody));
        var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.CreateQrSessionAsync(TestContext.Current.CancellationToken));
    }
}
