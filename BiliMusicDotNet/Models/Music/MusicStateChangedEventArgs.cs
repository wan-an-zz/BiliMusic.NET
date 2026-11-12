using System;
using System.Collections.Generic;

namespace BiliMusicDotNet.Models.Music;

/// <summary>
/// 播放列表、当前曲目或播放状态变化时的事件参数；列表字段是快照，事件触发后继续变化不会影响它。
/// </summary>
public sealed class MusicStateChangedEventArgs(
    IReadOnlyList<MusicTrack> playlist,
    MusicTrack? current,
    MusicPlaybackState state,
    string? message = null) : EventArgs
{
    /// <summary>变化后的播放列表快照。</summary>
    public IReadOnlyList<MusicTrack> Playlist { get; } = playlist;

    /// <summary>变化后的当前曲目；没有时为 <see langword="null"/>。</summary>
    public MusicTrack? Current { get; } = current;

    /// <summary>变化后的播放状态。</summary>
    public MusicPlaybackState State { get; } = state;

    /// <summary>可选的说明文本（如失败原因）。</summary>
    public string? Message { get; } = message;
}
