using System.Collections.Generic;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 内存版凭据存储：记录调用次数，可配置落盘失败。
/// </summary>
/// <remarks>
/// 走到"登录成功 / 登出"的用例必须注入本类：真实的 <c>CredentialService</c> 会写开发机自己的用户目录。
/// </remarks>
public sealed class FakeCredentialStore : ICredentialService
{
    /// <summary>每次保存的凭据，按顺序记录。</summary>
    public List<BiliLoginCredentials> Saved { get; } = [];

    /// <summary>清理次数。</summary>
    public int ClearCount { get; private set; }

    /// <summary>非空时，保存与清理都抛出它（模拟磁盘不可写）。</summary>
    public StorageException? ThrowOnWrite { get; init; }

    /// <inheritdoc />
    public BiliLoginCredentials? Current { get; private set; }

    /// <inheritdoc />
    public Task<BiliLoginCredentials?> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Current);

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
