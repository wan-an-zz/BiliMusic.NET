using System.Linq;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 用户信息抓取：昵称、头像地址（统一升 https）、大会员与等级。
/// </summary>
/// <remarks>
/// 这条链路验证的是"登录成功后能不能拿到人"。凭据的 Cookie 域若写错，
/// 表现就是这里拿不到昵称头像，所以断言里包含"凭据确实发往了 api 域"。
/// </remarks>
public sealed class UserProfileTests
{
    [Fact]
    public async Task 用户信息_登录成功后抓取昵称与头像并升为https()
    {
        var handler = FakeBiliHandler.Succeeding();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, new FakeCredentialStore());
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.WaitForConfirmationAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        var profile = Assert.IsType<BiliUserProfile>(result.Profile);
        Assert.Equal(123456, profile.Mid);
        Assert.Equal("测试用户", profile.Nickname);
        Assert.Equal("https://i0.hdslb.com/bfs/face/demo.jpg", profile.AvatarUri?.ToString());
        Assert.True(profile.IsVip);
        Assert.Equal(6, profile.Level);
        Assert.Equal(profile, service.CurrentProfile);
    }

    [Fact]
    public async Task 用户信息_凭据通过Cookie容器发往api域()
    {
        var handler = FakeBiliHandler.Succeeding();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, new FakeCredentialStore());
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        // 这一步验证 Cookie 域是否正确：.bilibili.com 的 Cookie 必须能发到 api.bilibili.com，
        // 否则"登录成功但取不到用户信息"。
        var navRequest = handler.Requests.Single(r => r.Uri.AbsoluteUri.Contains("/x/web-interface/nav"));
        Assert.Contains("SESSDATA=demo-sessdata-value", navRequest.Cookie ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("bili_jct=demo-jct-value", navRequest.Cookie ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("https://www.bilibili.com/", navRequest.Referer);
    }

    [Fact]
    public async Task 用户信息_资料接口失败不影响登录成立()
    {
        var handler = new FakeBiliHandler(url => url.Contains("generate")
            ? Responses.Json(Responses.GenerateBody)
            : url.Contains("/qrcode/poll")
                ? Responses.Redirect(Responses.SetCookies)
                : throw new HttpRequestException("模拟资料接口不可用"));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, new FakeCredentialStore());
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.WaitForConfirmationAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Profile);
        Assert.NotNull(result.Credentials);
        Assert.True(service.IsLoggedIn);
    }

    [Fact]
    public async Task 用户信息_凭据到手但nav判定未登录时不谎报成功()
    {
        var handler = new FakeBiliHandler(url => url.Contains("generate")
            ? Responses.Json(Responses.GenerateBody)
            : url.Contains("/qrcode/poll")
                ? Responses.Json(Responses.PollBody(Responses.StatusSuccess), Responses.SetCookies)
                : Responses.Json(Responses.NavNotLoggedIn));
        var store = new FakeCredentialStore();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, store);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.WaitForConfirmationAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.Expired, result.Outcome);
        Assert.False(service.IsLoggedIn, "校验未通过时必须清空登录态，不能让 UI 继续显示已登录");
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task 用户信息_触发StateChanged与LoginSucceeded()
    {
        var handler = FakeBiliHandler.Succeeding();
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance, new FakeCredentialStore());

        var states = new List<LoginState>();
        LoginSucceededEventArgs? succeeded = null;
        service.StateChanged += (_, e) => states.Add(e.State);
        service.LoginSucceeded += (_, e) => succeeded = e;

        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);
        var result = await service.WaitForConfirmationAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(LoginState.WaitingForScan, states);
        Assert.Equal(LoginState.LoggedIn, states[^1]);
        Assert.Equal(LoginState.LoggedIn, service.State);

        var payload = Assert.IsType<LoginSucceededEventArgs>(succeeded);
        // 事件只带用户信息：凭据由 ICredentialService 持有，不再经事件外流。
        Assert.NotNull(payload.Profile);
        Assert.Equal(result.Profile, payload.Profile);
    }
}
