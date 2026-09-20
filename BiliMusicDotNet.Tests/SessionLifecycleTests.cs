using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 登录态恢复、登出与资源释放。
/// </summary>
/// <remarks>
/// 恢复登录态的凭据由登录服务自己从凭据服务读盘取得，用例只需把"磁盘上有什么"预置进假存储。
/// </remarks>
public sealed class SessionLifecycleTests
{
    /// <summary>预置在"磁盘"上的凭据。</summary>
    private static BiliLoginCredentials SampleCredentials => new(
        "demo-sessdata-value",
        "demo-jct-value",
        "123456",
        "SESSDATA=demo-sessdata-value; bili_jct=demo-jct-value; DedeUserID=123456");

    [Fact]
    public async Task 恢复登录态_读到凭据且校验通过()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavLoggedIn));
        var store = new FakeCredentialStore { Stored = SampleCredentials };
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, store);

        var restored = await service.TryRestoreSessionAsync(TestContext.Current.CancellationToken);

        Assert.True(restored);
        Assert.True(service.IsLoggedIn);
        Assert.Equal(LoginState.LoggedIn, service.State);
        Assert.Equal(123456, service.CurrentProfile?.Mid);
        Assert.Equal(1, store.LoadCount);
        Assert.Contains("bili_jct=demo-jct-value", handler.Requests[^1].Cookie ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 恢复登录态_磁盘上没有凭据时不发请求()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavLoggedIn));
        var store = new FakeCredentialStore();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, store);

        var restored = await service.TryRestoreSessionAsync(TestContext.Current.CancellationToken);

        Assert.False(restored);
        Assert.False(service.IsLoggedIn);
        Assert.Equal(LoginState.Idle, service.State);
        Assert.Empty(handler.Requests);
        Assert.Equal(1, store.LoadCount);
    }

    [Fact]
    public async Task 恢复登录态_凭据失效()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavNotLoggedIn));
        var store = new FakeCredentialStore { Stored = SampleCredentials };
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, store);

        var restored = await service.TryRestoreSessionAsync(TestContext.Current.CancellationToken);

        Assert.False(restored);
        Assert.False(service.IsLoggedIn, "恢复失败不应残留登录态");
        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task 恢复登录态_凭据不完整时直接拒绝()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.NavLoggedIn));
        var store = new FakeCredentialStore
        {
            Stored = new BiliLoginCredentials("only-sessdata", string.Empty, string.Empty, string.Empty),
        };
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, store);

        var restored = await service.TryRestoreSessionAsync(TestContext.Current.CancellationToken);

        Assert.False(restored);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, store.ClearCount);
    }

    [Fact]
    public async Task 登出_清空本地状态并触发事件()
    {
        var handler = FakeBiliHandler.Succeeding();
        var credentials = new FakeCredentialStore();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, credentials);

        LoggedOutEventArgs? loggedOut = null;
        service.LoggedOut += (_, e) => loggedOut = e;

        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);
        await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(service.IsLoggedIn);

        service.Logout();

        Assert.False(service.IsLoggedIn);
        Assert.Null(service.CurrentProfile);
        Assert.Equal(LoginState.Idle, service.State);
        Assert.NotNull(loggedOut);
        Assert.Equal(1, credentials.ClearCount);
        Assert.Null(credentials.Current);
    }

    [Fact]
    public async Task 释放_后续调用抛ObjectDisposedException()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.GenerateBody));
        var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, new FakeCredentialStore());

        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.CreateQrSessionAsync(TestContext.Current.CancellationToken));
    }
}
