using System.IO;
using System.Security.Cryptography;
using System.Text;
using BiliMusicDotNet.Desktop.Services;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// Windows 端凭据加密（DPAPI）的往返与失败边界。
/// </summary>
/// <remarks>
/// 被测类来自桌面端项目，靠链接源码编进本程序集（见 csproj 说明）；
/// 共享侧的落盘行为见 <see cref="CredentialStorageTests"/>。
/// </remarks>
public sealed class EncryptServiceTests
{
    [Fact]
    public void 加密后能解回原文()
    {
        var plaintext = Encoding.UTF8.GetBytes("""{"sessData":"demo","dedeUserId":"123456"}""");
        using var input = new MemoryStream(plaintext);

        var ciphertext = new DpapiEncryptService().Encrypt(input);

        using var cipher = new MemoryStream(ciphertext);
        Assert.Equal(plaintext, new DpapiEncryptService().Decrypt(cipher));
    }

    [Fact]
    public void 密文与明文不同且看不出原文()
    {
        const string secret = "SESSDATA=demo-sessdata-value";
        var plaintext = Encoding.UTF8.GetBytes(secret);
        using var input = new MemoryStream(plaintext);

        var ciphertext = new DpapiEncryptService().Encrypt(input);

        Assert.NotEqual(plaintext, ciphertext);
        Assert.DoesNotContain("demo-sessdata-value", Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public void 同一份明文两次加密得到不同密文()
    {
        // DPAPI 自带随机盐：密文不可比，攻击者也就无法靠比对判断凭据有没有变。
        Assert.NotEqual(Encrypt("same"), Encrypt("same"));
    }

    [Fact]
    public void 不是本应用加密出来的字节解不开()
    {
        using var cipher = new MemoryStream([0x01, 0x02, 0x03, 0x04]);

        // 抛 CryptographicException 而不是返回垃圾，凭据服务才能把它翻译成"未登录"。
        Assert.Throws<CryptographicException>(() => new DpapiEncryptService().Decrypt(cipher));
    }

    [Fact]
    public void 从流的当前位置开始读()
    {
        // 接口约定"从当前位置读到末尾"：前面垫的字节不能被算进密文。
        var ciphertext = Encrypt("position");
        using var stream = new MemoryStream([0xFF, 0xFF, .. ciphertext]) { Position = 2 };

        Assert.Equal(Encoding.UTF8.GetBytes("position"), new DpapiEncryptService().Decrypt(stream));
    }

    [Fact]
    public async Task 与凭据服务串起来能密文落盘并原样读回()
    {
        using var directory = new TempDirectory();

        // 两侧都用真实实现：这条链路才覆盖了"磁盘上真的是密文、读回来真的是凭据"。
        var writer = NewStore(directory);
        await writer.SaveAsync(Credentials, TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(
            CredentialService.ResolveFilePath(directory.Path),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("demo-sessdata-value", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var reader = NewStore(directory);
        Assert.True(await reader.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Credentials, reader.Current);
    }

    /// <summary>测试用凭据。</summary>
    private static BiliLoginCredentials Credentials => new(
        "demo-sessdata-value",
        "demo-jct-value",
        "123456",
        "SESSDATA=demo-sessdata-value; bili_jct=demo-jct-value; DedeUserID=123456");

    /// <summary>临时目录下、用真实加密实现的凭据服务。</summary>
    /// <param name="directory">临时应用数据根目录。</param>
    /// <returns>凭据服务。</returns>
    private static CredentialService NewStore(TempDirectory directory)
        => new(directory.Path, new DpapiEncryptService(), NullLogger<CredentialService>.Instance);

    /// <summary>加密一段文本。</summary>
    /// <param name="text">明文。</param>
    /// <returns>密文。</returns>
    private static byte[] Encrypt(string text)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return new DpapiEncryptService().Encrypt(input);
    }
}
