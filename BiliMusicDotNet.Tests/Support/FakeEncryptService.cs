using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BiliMusicDotNet.Abstracts;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 测试用的可逆加解密替身：逐字节异或，密文必然与明文不同，且不需要任何平台能力。
/// </summary>
/// <remarks>
/// 异或的逆运算就是它自己，因此加解密共用一份实现；这也让"写进去的和读出来的是同一套变换"不成问题。
/// </remarks>
public sealed class FakeEncryptService : IEncryptService
{
    private const byte Mask = 0x5A;

    /// <summary>每次加密收到的明文，按顺序记录。</summary>
    public List<byte[]> PlaintextsSeen { get; } = [];

    /// <summary>加密次数。</summary>
    public int EncryptCount { get; private set; }

    /// <summary>解密次数。</summary>
    public int DecryptCount { get; private set; }

    /// <summary>为真时加密抛 <see cref="CryptographicException"/>，模拟平台加密失败。</summary>
    public bool ThrowOnEncrypt { get; init; }

    /// <summary>为真时解密抛 <see cref="CryptographicException"/>，模拟换机器或密钥失效。</summary>
    public bool ThrowOnDecrypt { get; init; }

    /// <inheritdoc />
    public byte[] Encrypt(Stream plaintext)
    {
        EncryptCount++;
        var bytes = ReadAll(plaintext);
        PlaintextsSeen.Add(bytes);

        if (ThrowOnEncrypt)
        {
            throw new CryptographicException("模拟加密失败");
        }

        return Transform(bytes);
    }

    /// <inheritdoc />
    public byte[] Decrypt(Stream ciphertext)
    {
        DecryptCount++;

        if (ThrowOnDecrypt)
        {
            throw new CryptographicException("模拟解密失败");
        }

        return Transform(ReadAll(ciphertext));
    }

    /// <summary>把流读成字节数组。</summary>
    /// <param name="payload">待读取的流。</param>
    /// <returns>流中剩余内容。</returns>
    private static byte[] ReadAll(Stream payload)
    {
        using var buffer = new MemoryStream();
        payload.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>逐字节异或。</summary>
    /// <param name="bytes">输入。</param>
    /// <returns>输出。</returns>
    private static byte[] Transform(byte[] bytes)
    {
        var result = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            result[i] = (byte)(bytes[i] ^ Mask);
        }

        return result;
    }
}
