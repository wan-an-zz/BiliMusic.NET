using System;
using System.IO;
using System.Security.Cryptography;
using Android.Security.Keystore;
using BiliMusicDotNet.Abstracts;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

// Javax.Crypto 与 System.Security.Cryptography 各有一个 CipherMode，这里只可能用前者。
using CipherMode = Javax.Crypto.CipherMode;

namespace BiliMusicDotNet.Android.Services;

/// <summary>
/// Android 端的凭据加密：密钥由系统密钥库保管，密文用 AES-GCM 生成。
/// </summary>
/// <remarks>
/// 密钥在首次使用时生成，之后只以别名形式存在，应用拿不到密钥材料本身。
/// 输出布局为 <c>[12 字节 IV][密文 + 16 字节认证标签]</c>：GCM 每次加密都必须换 IV，因此随密文一起落盘。
/// </remarks>
public sealed class AndroidKeystoreEncryptService : IEncryptService
{
    /// <summary>系统密钥库里的密钥别名。</summary>
    private const string KeyAlias = "bili_login_credential";

    /// <summary>系统密钥库的服务名。</summary>
    private const string KeyStoreProvider = "AndroidKeyStore";

    /// <summary>AES-GCM 变换名；认证标签与 IV 长度按 GCM 的常规取值固定，解密端据此切分。</summary>
    private const string Transformation = "AES/GCM/NoPadding";

    private const int TagBits = 128;
    private const int IvLength = 12;
    private const int KeySizeBits = 256;

    /// <inheritdoc />
    public byte[] Encrypt(Stream plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        // IV 自己生成而不是取 Cipher.GetIV()：长度必须由本类说了算，解密端才能按固定偏移切分。
        var iv = new byte[IvLength];
        new SecureRandom().NextBytes(iv);

        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, iv));

            var payload = cipher.DoFinal(ReadAll(plaintext))!;

            var result = new byte[IvLength + payload.Length];
            iv.CopyTo(result, 0);
            payload.CopyTo(result, IvLength);
            return result;
        }
        catch (GeneralSecurityException ex)
        {
            throw new CryptographicException("凭据加密失败", ex);
        }
    }

    /// <inheritdoc />
    public byte[] Decrypt(Stream ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var payload = ReadAll(ciphertext);
        if (payload.Length <= IvLength)
        {
            throw new CryptographicException("凭据密文太短，IV 不完整");
        }

        var iv = new byte[IvLength];
        Array.Copy(payload, iv, IvLength);

        var body = new byte[payload.Length - IvLength];
        Array.Copy(payload, IvLength, body, 0, body.Length);

        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, GetOrCreateKey(), new GCMParameterSpec(TagBits, iv));

            // 密文被改过或换过密钥时，认证标签校验不过，DoFinal 会抛。
            return cipher.DoFinal(body)!;
        }
        catch (GeneralSecurityException ex)
        {
            throw new CryptographicException("凭据解密失败", ex);
        }
    }

    /// <summary>
    /// 取出别名对应的密钥，没有就新建一个。
    /// </summary>
    /// <returns>密钥库持有的 AES 密钥。</returns>
    private static ISecretKey GetOrCreateKey()
    {
        var keyStore = KeyStore.GetInstance(KeyStoreProvider)!;
        keyStore.Load(null);

        if (keyStore.ContainsAlias(KeyAlias))
        {
            return (ISecretKey)keyStore.GetKey(KeyAlias, null)!;
        }

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreProvider)!;
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)!
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(KeySizeBits)!
            .Build()!;

        generator.Init(spec);
        return (ISecretKey)generator.GenerateKey()!;
    }

    /// <summary>
    /// 把流读成字节数组。
    /// </summary>
    /// <param name="payload">待读取的流。</param>
    /// <returns>流中剩余内容。</returns>
    private static byte[] ReadAll(Stream payload)
    {
        using var buffer = new MemoryStream();
        payload.CopyTo(buffer);
        return buffer.ToArray();
    }
}
