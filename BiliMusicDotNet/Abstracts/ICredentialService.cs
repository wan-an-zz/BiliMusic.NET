using System.Threading;
using System.Threading.Tasks;
using BiliMusicDotNet.Models.Login;

namespace BiliMusicDotNet.Abstracts;

/// <summary>
/// 登录凭据的持有者与持久化入口：内存态只此一份，磁盘上是明文 JSON。
/// </summary>
/// <remarks>
/// <see cref="Current"/> 是进程内的唯一读取入口；进程重启后它为空，必须先 <see cref="LoadAsync"/>。
/// 凭据只在与 B 站域名通信时使用，禁止写进日志或拼进 URL。
/// </remarks>
public interface ICredentialService
{
    /// <summary>当前凭据；未登录或已清理时为 <see langword="null"/>。</summary>
    BiliLoginCredentials? Current { get; }

    /// <summary>
    /// 从磁盘读回凭据，成功后写入 <see cref="Current"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 读到的凭据；文件不存在、内容损坏或凭据不完整时返回 <see langword="null"/>（此时 <see cref="Current"/> 保持不变）。
    /// </returns>
    Task<BiliLoginCredentials?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 覆盖写入凭据：先更新 <see cref="Current"/>，再落盘。
    /// </summary>
    /// <param name="credentials">要保存的凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 落盘失败会抛异常，但 <see cref="Current"/> 已是新值——内存态与磁盘态可能不一致，
    /// 调用方若不接受这一点，需在捕获异常后自行清理。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="credentials"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="Models.Exceptions.StorageException">建目录、写文件或序列化失败。</exception>
    Task SaveAsync(BiliLoginCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空 <see cref="Current"/> 并删除磁盘副本；文件本就不存在时视为成功。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="Models.Exceptions.StorageException">删除失败。</exception>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
