using System.IO;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 凭据持久化：明文 JSON 的写入、读回、清理与失败边界。
/// </summary>
/// <remarks>
/// 全部用例都指向临时目录：默认路径只是被"算"出来做断言，不实际写用户目录。
/// </remarks>
public sealed class CredentialStorageTests
{
    /// <summary>测试用凭据：四个字段各不相同，便于发现错位。</summary>
    private static BiliLoginCredentials SampleCredentials => new(
        "demo-sessdata-value",
        "demo-jct-value",
        "123456",
        "SESSDATA=demo-sessdata-value; bili_jct=demo-jct-value; DedeUserID=123456");

    [Fact]
    public async Task 保存后读回全部字段一致()
    {
        using var directory = new TempDirectory();
        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);

        var saved = SampleCredentials;
        await service.SaveAsync(saved, TestContext.Current.CancellationToken);

        // 换一个实例读，确保读到的是磁盘内容而不是内存缓存。
        var reader = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);
        var loaded = await reader.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(loaded);
        Assert.Equal(saved, reader.Current);
        Assert.Equal("demo-jct-value", reader.Current?.BiliJct);
        Assert.Contains("DedeUserID=123456", reader.Current?.CookieHeader ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 读回的结果放在Current上()
    {
        using var directory = new TempDirectory();
        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);
        Assert.Null(service.Current);

        await service.SaveAsync(SampleCredentials, TestContext.Current.CancellationToken);
        Assert.Equal(SampleCredentials, service.Current);

        // 新实例的 Current 为空，只有读盘成功之后才有值。
        var reader = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);
        Assert.Null(reader.Current);

        Assert.True(await reader.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SampleCredentials, reader.Current);
    }

    [Fact]
    public void 默认路径落在LocalApplicationData下的LoginCredential目录()
    {
        const string baseDirectory = @"C:\Users\demo\AppData\Local";

        var path = CredentialService.ResolveFilePath(baseDirectory);

        Assert.Equal(
            Path.Combine(baseDirectory, "BiliMusicDotNet", "LoginCredential", "credentials.json"),
            path);
        Assert.EndsWith("credentials.json", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 落盘内容为字段与取值的明文JSON()
    {
        using var directory = new TempDirectory();
        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);

        await service.SaveAsync(SampleCredentials, TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(
            CredentialService.ResolveFilePath(directory.Path),
            TestContext.Current.CancellationToken);

        // 字段名固定：改名即等于旧文件读不回来。
        Assert.Contains("\"sessData\"", json, StringComparison.Ordinal);
        Assert.Contains("\"biliJct\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dedeUserId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cookieHeader\"", json, StringComparison.Ordinal);
        Assert.Contains("\"demo-sessdata-value\"", json, StringComparison.Ordinal);

        // 派生属性不落盘：文件里只有输入字段，读回结果才可复现。
        Assert.DoesNotContain("isComplete", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 文件不存在时返回false且不创建目录()
    {
        using var directory = new TempDirectory();
        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);

        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(loaded);
        Assert.Null(service.Current);
        Assert.False(Directory.Exists(Path.GetDirectoryName(CredentialService.ResolveFilePath(directory.Path))));
    }

    [Fact]
    public async Task JSON损坏时返回false且不抛异常()
    {
        using var directory = new TempDirectory();
        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);
        WriteRaw(directory.Path, "{ 这不是合法 JSON");

        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(loaded);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task 凭据不完整时返回false()
    {
        using var directory = new TempDirectory();
        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);
        WriteRaw(directory.Path, """{"sessData":"only-sessdata","biliJct":"","dedeUserId":"","cookieHeader":""}""");

        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(loaded);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task 清理后Current为空且重复清理幂等()
    {
        using var directory = new TempDirectory();
        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);
        await service.SaveAsync(SampleCredentials, TestContext.Current.CancellationToken);
        var filePath = CredentialService.ResolveFilePath(directory.Path);

        await service.ClearAsync(TestContext.Current.CancellationToken);
        Assert.Null(service.Current);
        Assert.False(File.Exists(filePath));

        // 再清一次不应抛异常（文件已不存在）。
        await service.ClearAsync(TestContext.Current.CancellationToken);
        Assert.False(await service.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task 目录不可写时抛持久化异常且Current仍为新值()
    {
        using var directory = new TempDirectory();

        // 在"目录"位置上放一个同名文件，创建目录必然失败。
        Directory.CreateDirectory(Path.Combine(directory.Path, "BiliMusicDotNet"));
        File.WriteAllText(Path.Combine(directory.Path, "BiliMusicDotNet", "LoginCredential"), "占位");

        var service = new CredentialService(directory.Path, NullLogger<CredentialService>.Instance);

        var exception = await Assert.ThrowsAsync<StorageException>(
            () => service.SaveAsync(SampleCredentials, TestContext.Current.CancellationToken));

        Assert.Equal("写入", exception.Operation);
        Assert.Contains("credentials.json", exception.Path, StringComparison.Ordinal);
        Assert.Equal(SampleCredentials, service.Current);
    }

    /// <summary>按服务约定的路径直接写入原始文本，用于构造损坏文件。</summary>
    /// <param name="baseDirectory">应用数据根目录。</param>
    /// <param name="content">文件内容。</param>
    private static void WriteRaw(string baseDirectory, string content)
    {
        var filePath = CredentialService.ResolveFilePath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content);
    }
}
