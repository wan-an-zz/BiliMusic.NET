using System.Net;
using System.Text;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 取码环节：<see cref="IBiliLoginService.CreateQrSessionAsync"/> 的请求特征与失败处理。
/// </summary>
public sealed class QrSessionTests
{
    [Fact]
    public async Task 取码_返回二维码内容与轮询凭据()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.GenerateBody));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        // 二维码内容原样交给 UI 渲染；轮询凭据由服务保管，UI 不需要接触。
        Assert.Equal(Responses.QrUrl, session.Content);
        Assert.Equal(Responses.QrKey, session.Key);
        Assert.True(
            session.ExpiresAt > DateTimeOffset.Now.AddMinutes(2),
            "二维码有效期应约为 3 分钟");
    }

    [Fact]
    public async Task 取码_推进状态并触发StateChanged()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.GenerateBody));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);
        var states = new List<LoginState>();
        service.StateChanged += (_, e) => states.Add(e.State);

        await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LoginState.WaitingForScan, service.State);
        Assert.Equal([LoginState.WaitingForScan], states);
    }

    [Fact]
    public async Task 取码_请求带passport域Referer与浏览器UA()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json(Responses.GenerateBody));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://passport.bilibili.com/x/passport-login/web/qrcode/generate",
            request.Uri.AbsoluteUri);
        Assert.Equal("https://passport.bilibili.com/login", request.Referer);
        Assert.Contains("Chrome/", request.UserAgent ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-412, "请求被拦截")]
    [InlineData(-101, "账号未登录")]
    public async Task 取码_业务码非零时抛BiliApiException(int code, string message)
    {
        var handler = new FakeBiliHandler(
            _ => Responses.Json($$"""{"code":{{code}},"message":"{{message}}","ttl":1}"""));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var exception = await Assert.ThrowsAsync<BiliApiException>(
            () => service.CreateQrSessionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(code, exception.Code);
        Assert.Equal(LoginState.Idle, service.State);
    }

    [Fact]
    public async Task 取码_响应缺少二维码字段时抛BiliApiException()
    {
        var handler = new FakeBiliHandler(_ => Responses.Json("""{"code":0,"message":"0","ttl":1,"data":{}}"""));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        await Assert.ThrowsAsync<BiliApiException>(() => service.CreateQrSessionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 取码_非JSON响应转为业务异常()
    {
        // 被风控页/代理拦截时返回的是 HTML，不能把 JsonException 直接抛给 UI。
        var handler = new FakeBiliHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>blocked</html>", Encoding.UTF8, "text/html"),
        });
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        await Assert.ThrowsAsync<BiliApiException>(() => service.CreateQrSessionAsync(TestContext.Current.CancellationToken));
    }
}
