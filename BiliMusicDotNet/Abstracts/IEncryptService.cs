using System;
using System.IO;
using System.Security.Cryptography;

namespace BiliMusicDotNet.Abstracts;

/// <summary>
/// 凭据落盘前的加解密入口，各平台 head 提供实现。
/// </summary>
/// <remarks>
/// 密钥材料由平台自己保管，接口不接受调用方传入密钥；因此密文只在本机本应用内可解。
/// 两个方法都从流的当前位置读到末尾，调用方不必先归零 <see cref="Stream.Position"/>。
/// </remarks>
public interface IEncryptService
{
    /// <summary>
    /// 加密。
    /// </summary>
    /// <param name="plaintext">待加密内容。</param>
    /// <returns>密文。</returns>
    /// <exception cref="CryptographicException">加密失败。</exception>
    /// <exception cref="PlatformNotSupportedException">当前平台没有可用实现。</exception>
    byte[] Encrypt(Stream plaintext);

    /// <summary>
    /// 解密。
    /// </summary>
    /// <param name="ciphertext">密文。</param>
    /// <returns>原文。</returns>
    /// <exception cref="CryptographicException">
    /// 内容不是本机本应用加密出来的密文（换机器、换密钥或文件损坏）。
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">当前平台没有可用实现。</exception>
    byte[] Decrypt(Stream ciphertext);
}
