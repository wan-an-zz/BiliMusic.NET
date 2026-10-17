using System.Collections.Generic;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 内存版凭据存储：记录调用次数，可预置"磁盘上有什么"，也可配置落盘失败。
/// </summary>
/// <remarks>
/// 走到"登录成功 / 登出 / 恢复登录态"的用例必须注入本类：
/// 真实的 <c>CredentialService</c> 会读写开发机自己的用户目录。
/// </remarks>
public sealed class FakeCredentialStore : ICredentialService
{
    /// <summary>每次保存的凭据，按顺序记录。</summary>
    public List<BiliLoginCredentials> Saved { get; } = [];

    /// <summary>清理次数。</summary>
    public int ClearCount { get; private set; }

    /// <summary>读盘次数。</summary>
    public int LoadCount { get; private set; }

    /// <summary>非空时，保存与清理都抛出它（模拟磁盘不可写）。</summary>
    public StorageException? ThrowOnWrite { get; init; }

    /// <summary>模拟磁盘上已有的凭据；为 <see langword="null"/> 表示"没有存过"。</summary>
    public BiliLoginCredentials? Stored { get; init; }

    /// <inheritdoc />
    public BiliLoginCredentials? Current { get; private set; }

    /// <inheritdoc />
    public Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCount++;

        if (Stored is null)
        {
            return Task.FromResult(false);
        }

        Current = Stored;
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task SaveAsync(BiliLoginCredentials credentials, CancellationToken cancellationToken = default)
    {
        // 与真实实现一致：先更新 Current，落盘失败只影响磁盘那一份。
        Current = credentials;

        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        Saved.Add(credentials);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ClearCount++;
        Current = null;
        return Task.CompletedTask;
    }
}
