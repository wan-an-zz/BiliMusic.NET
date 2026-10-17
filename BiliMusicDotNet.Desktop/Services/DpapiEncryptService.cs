using System;
using System.IO;
using System.Security.Cryptography;
using BiliMusicDotNet.Abstracts;

namespace BiliMusicDotNet.Desktop.Services;

/// <summary>
/// Windows 端的凭据加密：DPAPI，密钥由系统按当前用户派生和管理。
/// </summary>
/// <remarks>
/// 作用域是 <see cref="DataProtectionScope.CurrentUser"/>，密文换用户或换机器都解不开。
/// 附加熵只是给密文加一层应用隔离，本身不是秘密，改动它等于让已有密文作废。
/// </remarks>
public sealed class DpapiEncryptService : IEncryptService
{
    /// <summary>应用专属附加熵，防止同一用户下别的程序解开这份密文。</summary>
    private static readonly byte[] Entropy = "BiliMusicDotNet.LoginCredential"u8.ToArray();

    /// <inheritdoc />
    public byte[] Encrypt(Stream plaintext) => Protect(plaintext, encrypt: true);

    /// <inheritdoc />
    public byte[] Decrypt(Stream ciphertext) => Protect(ciphertext, encrypt: false);

    /// <summary>
    /// 把流读成字节数组后交给 DPAPI。
    /// </summary>
    /// <param name="payload">待处理内容。</param>
    /// <param name="encrypt">为真走加密，否则走解密。</param>
    /// <returns>处理结果。</returns>
    private static byte[] Protect(Stream payload, bool encrypt)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var buffer = new MemoryStream();
        payload.CopyTo(buffer);
        var bytes = buffer.ToArray();

        return encrypt
            ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)
            : ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
    }
}
