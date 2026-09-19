using System;

namespace BiliMusicDotNet.Models.Exceptions;

/// <summary>
/// 凭据的磁盘读写失败时抛出；文件不存在、内容损坏这类"读不到"的情况不抛，由读取方返回空值。
/// </summary>
/// <param name="operation">操作名称，形如"写入"。</param>
/// <param name="path">出错的文件路径。</param>
/// <param name="innerException">原始异常（IO / 权限 / 序列化）。</param>
public sealed class StorageException(string operation, string path, Exception innerException)
    : Exception($"登录凭据{operation}失败（{path}）", innerException)
{
    /// <summary>出错的文件路径。</summary>
    public string Path { get; } = path;

    /// <summary>操作名称（"写入"/"删除"）。</summary>
    public string Operation { get; } = operation;
}
