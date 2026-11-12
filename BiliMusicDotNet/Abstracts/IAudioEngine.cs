using System;
using BiliMusicDotNet.Models.Music;

namespace BiliMusicDotNet.Abstracts;

/// <summary>音频输出引擎；服务只依赖这个接口，测试与探针可替换为假实现。</summary>
internal interface IAudioEngine : IDisposable
{
    /// <summary>当前是否已成功创建音频流。</summary>
    bool HasStream { get; }

    /// <summary>当前流是否已播放到结尾或因连接中断结束。</summary>
    bool HasEnded { get; }

    /// <summary>当前流代次；每次建流与释放都会递增，用于丢弃迟到事件。</summary>
    int Generation { get; }

    /// <summary>播放状态。</summary>
    MusicPlaybackState State { get; }

    /// <summary>当前播放位置（已加上建流时的基准位置）。</summary>
    TimeSpan Position { get; }

    /// <summary>当前在音频文件中的绝对字节位置。</summary>
    long PositionBytes { get; }

    /// <summary>当前流总时长；未知时为零。</summary>
    TimeSpan Duration { get; }

    /// <summary>播放到结尾时触发，参数携带触发时的流代次。</summary>
    event EventHandler<AudioPlaybackEndedEventArgs>? PlaybackEnded;

    /// <summary>初始化音频设备；可重复调用。</summary>
    void Initialize();

    /// <summary>用 URL 创建并载入新流；成功后才释放旧流。</summary>
    long Load(string url, long offsetBytes = 0, TimeSpan basePosition = default);

    /// <summary>开始或继续播放。</summary>
    void Play(bool restart = false);

    /// <summary>暂停。</summary>
    void Pause();

    /// <summary>停止；保留当前流。</summary>
    void Stop();

    /// <summary>停止并释放当前流。</summary>
    void Unload();
}

/// <summary>音频流播放到结尾的事件参数。</summary>
internal sealed class AudioPlaybackEndedEventArgs(int generation) : EventArgs
{
    /// <summary>触发事件的流代次。</summary>
    public int Generation { get; } = generation;
}
