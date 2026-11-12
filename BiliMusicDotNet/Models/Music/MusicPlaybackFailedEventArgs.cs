using System;

namespace BiliMusicDotNet.Models.Music;

/// <summary>用户可见的加载或播放失败；预取失败不会触发。</summary>
public sealed class MusicPlaybackFailedEventArgs(
    string bvid,
    string message,
    Exception? exception = null) : EventArgs
{
    /// <summary>失败的曲目 BV 号。</summary>
    public string Bvid { get; } = bvid;

    /// <summary>可直接展示的错误说明。</summary>
    public string Message { get; } = message;

    /// <summary>原始异常；没有时为 <see langword="null"/>。</summary>
    public Exception? Exception { get; } = exception;
}
