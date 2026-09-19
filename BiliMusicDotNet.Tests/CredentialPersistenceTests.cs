using System.IO;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 扫码登录与凭据持久化的联动：登录成功落盘、登出与失效清理。
/// </summary>
/// <remarks>
/// 这里用真实的 <see cref="CredentialService"/> 配临时目录，验证的是"服务之间接得对不对"；
/// 凭据服务自身的读写细节见 <see cref="CredentialStorageTests"/>。
/// </remarks>
public sealed class CredentialPersistenceTests
{
    [Fact]
    public async Task 登录成功后凭据落盘一份()
    {
        using var directory = new TempDirectory();
        var store = NewStore(directory);
        using var service = NewLoginService(FakeBiliHandler.Succeeding(), store);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.WaitForConfirmationAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.Succeeded, result.Outcome);
        Assert.True(await TempDirectory.WaitForFileAsync(CredentialService.ResolveFilePath(directory.Path)));

        // 换一个实例从磁盘读回，确认落的是可用的凭据而不是只有内存里有。
        var reader = NewStore(directory);
        var loaded = await reader.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("123456", loaded?.DedeUserId);
        Assert.True(loaded?.IsComplete);
        Assert.Equal(store.Current, loaded);
    }

    [Fact]
    public async Task 登录成功后磁盘上没有tmp残留()
    {
        using var directory = new TempDirectory();
        var store = NewStore(directory);
        using var service = NewLoginService(FakeBiliHandler.Succeeding(), store);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var filePath = CredentialService.ResolveFilePath(directory.Path);
        Assert.True(await TempDirectory.WaitForFileAsync(filePath));
        Assert.False(File.Exists(filePath + ".tmp"));
    }

    [Fact]
    public async Task 登出后凭据文件被删除且Current为空()
    {
        using var directory = new TempDirectory();
        var store = NewStore(directory);
        using var service = NewLoginService(FakeBiliHandler.Succeeding(), store);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);
        await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var filePath = CredentialService.ResolveFilePath(directory.Path);
        Assert.True(await TempDirectory.WaitForFileAsync(filePath));

        service.Logout();

        Assert.False(File.Exists(filePath));
        Assert.Null(store.Current);
        Assert.False(service.IsLoggedIn);
    }

    [Fact]
    public async Task nav判定失效时清理已落盘的副本()
    {
        using var directory = new TempDirectory();
        var store = NewStore(directory);
        var handler = FakeBiliHandler.Succeeding();
        using var service = NewLoginService(handler, store);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);
        await service.WaitForConfirmationAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var filePath = CredentialService.ResolveFilePath(directory.Path);
        Assert.True(await TempDirectory.WaitForFileAsync(filePath));

        // 让后续 nav 返回 -101：登录态已失效，磁盘副本必须一并清掉。
        handler.Responder = _ => Responses.Json(Responses.NavNotLoggedIn);
        var profile = await service.GetProfileAsync(TestContext.Current.CancellationToken);

        Assert.Null(profile);
        Assert.False(File.Exists(filePath));
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task 落盘失败不影响登录成立()
    {
        var failure = new StorageException("写入", @"X:\不可写\credentials.json", new IOException("模拟磁盘不可写"));
        var store = new FakeCredentialStore { ThrowOnWrite = failure };
        using var service = NewLoginService(FakeBiliHandler.Succeeding(), store);

        LoginSucceededEventArgs? succeeded = null;
        service.LoginSucceeded += (_, e) => succeeded = e;

        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);
        var result = await service.WaitForConfirmationAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        // 用户在手机上确实确认过了：磁盘写不进去也不能把这次登录判成失败。
        Assert.Equal(QrLoginOutcome.Succeeded, result.Outcome);
        Assert.True(service.IsLoggedIn);
        Assert.NotNull(succeeded);
    }

    [Fact]
    public async Task 未注入持久化能力之外的凭据服务时登录照常工作()
    {
        // 用假存储模拟"内存里能拿到凭据、磁盘上什么都没有"的最小实现。
        var store = new FakeCredentialStore();
        using var service = NewLoginService(FakeBiliHandler.Succeeding(), store);
        var session = await service.CreateQrSessionAsync(TestContext.Current.CancellationToken);

        var result = await service.WaitForConfirmationAsync(
            session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(QrLoginOutcome.Succeeded, result.Outcome);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(result.Credentials, saved);
    }

    /// <summary>临时目录下的真实凭据服务。</summary>
    /// <param name="directory">临时应用数据根目录。</param>
    /// <returns>凭据服务。</returns>
    private static CredentialService NewStore(TempDirectory directory)
        => new(directory.Path, NullLogger<CredentialService>.Instance);

    /// <summary>装配登录服务。</summary>
    /// <param name="handler">假 HTTP 传输。</param>
    /// <param name="store">凭据服务。</param>
    /// <returns>登录服务。</returns>
    private static BiliLoginService NewLoginService(FakeBiliHandler handler, ICredentialService store)
        => new(handler, NullLogger<BiliLoginService>.Instance, store);
}
