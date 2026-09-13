using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 头像下载：以原始字节交 UI 解码，服务层不引用任何图像类型。
/// </summary>
public sealed class AvatarTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task 头像_以字节返回并带上CDN所需的Referer()
    {
        var handler = new FakeBiliHandler(_ => Responses.Bytes(PngHeader));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var bytes = await service.GetAvatarImageAsync(
            new Uri("https://i0.hdslb.com/bfs/face/demo.jpg"), TestContext.Current.CancellationToken);

        Assert.Equal(PngHeader, bytes);
        Assert.Equal("https://www.bilibili.com/", handler.Requests[0].Referer);
    }

    [Fact]
    public async Task 头像_地址为空时返回空而不发请求()
    {
        var handler = new FakeBiliHandler(_ => Responses.Bytes(PngHeader));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        Assert.Null(await service.GetAvatarImageAsync(null, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task 头像_下载失败时返回空而不抛异常(int statusCode)
    {
        var handler = new FakeBiliHandler(
            _ => new HttpResponseMessage((System.Net.HttpStatusCode)statusCode));
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var bytes = await service.GetAvatarImageAsync(
            new Uri("https://i0.hdslb.com/bfs/face/demo.jpg"), TestContext.Current.CancellationToken);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task 头像_网络异常时返回空而不拖垮登录流程()
    {
        var handler = new FakeBiliHandler(_ => Responses.Bytes(PngHeader))
        {
            ThrowOnSend = new HttpRequestException("模拟 CDN 不可达"),
        };
        using var service = new BiliLoginService(handler, NullLogger<BiliLoginService>.Instance);

        var bytes = await service.GetAvatarImageAsync(
            new Uri("https://i0.hdslb.com/bfs/face/demo.jpg"), TestContext.Current.CancellationToken);

        Assert.Null(bytes);
    }
}
