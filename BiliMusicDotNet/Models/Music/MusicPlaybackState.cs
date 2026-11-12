namespace BiliMusicDotNet.Models.Music;

/// <summary>播放状态的对外取值。</summary>
public enum MusicPlaybackState
{
    /// <summary>没有载入任何曲目，或播放列表已清空。</summary>
    Idle,

    /// <summary>正在解析地址或创建音频流。</summary>
    Loading,

    /// <summary>正在播放。</summary>
    Playing,

    /// <summary>已暂停，保留当前流与播放位置。</summary>
    Paused,

    /// <summary>当前曲目已播完，或引擎已停止。</summary>
    Stopped,

    /// <summary>最近一次加载或播放失败。</summary>
    Failed,
}
