using System;
using System.Collections.Generic;
using System.Linq;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Music;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 内存音频引擎：记录每次建流的 URL 与偏移，并可手动触发播完事件。
/// </summary>
internal sealed class FakeAudioEngine : IAudioEngine
{
    /// <summary>按顺序记录的建流调用。</summary>
    public List<LoadCall> Loads { get; } = [];

    /// <summary>下次 Load 要抛出的异常；为空表示成功。</summary>
    public Queue<Exception> LoadFailures { get; } = new();

    /// <summary>Initialize 被调用的次数。</summary>
    public int InitializeCount { get; private set; }

    /// <summary>Play 被调用的次数。</summary>
    public int PlayCount { get; private set; }

    /// <summary>按顺序记录的建流 URL（去掉附加的 HTTP 头）。</summary>
    public IReadOnlyList<string> LoadedUrls => Loads
        .Select(call => call.Url.Split("\r\n", StringSplitOptions.None)[0])
        .ToArray();

    /// <summary>Dispose 是否已调用。</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public bool HasStream { get; private set; }

    /// <inheritdoc />
    public bool HasEnded { get; private set; }

    /// <inheritdoc />
    public int Generation { get; private set; }

    /// <inheritdoc />
    public MusicPlaybackState State { get; private set; } = MusicPlaybackState.Idle;

    /// <inheritdoc />
    public TimeSpan Position { get; set; }

    /// <inheritdoc />
    public long PositionBytes { get; set; }

    /// <inheritdoc />
    public TimeSpan Duration { get; set; }

    /// <inheritdoc />
    public event EventHandler<AudioPlaybackEndedEventArgs>? PlaybackEnded;

    /// <inheritdoc />
    public void Initialize() => InitializeCount++;

    /// <inheritdoc />
    public long Load(string url, long offsetBytes = 0, TimeSpan basePosition = default)
    {
        if (LoadFailures.Count > 0)
        {
            throw LoadFailures.Dequeue();
        }

        Loads.Add(new LoadCall(url, offsetBytes, basePosition));
        Generation++;
        HasStream = true;
        HasEnded = false;
        State = MusicPlaybackState.Stopped;
        Position = basePosition;
        PositionBytes = offsetBytes;
        return Generation;
    }

    /// <inheritdoc />
    public void Play(bool restart = false)
    {
        if (!HasStream)
        {
            throw new MusicPlaybackException("没有可播放的音频流。");
        }

        PlayCount++;
        State = MusicPlaybackState.Playing;
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (!HasStream)
        {
            throw new MusicPlaybackException("没有可暂停的音频流。");
        }

        State = MusicPlaybackState.Paused;
    }

    /// <inheritdoc />
    public void Stop()
    {
        State = MusicPlaybackState.Stopped;
    }

    /// <inheritdoc />
    public void Unload()
    {
        HasStream = false;
        HasEnded = false;
        State = MusicPlaybackState.Idle;
        Generation++;
        Position = TimeSpan.Zero;
        PositionBytes = 0;
    }

    /// <summary>模拟当前流自然播完。</summary>
    public void RaiseEnded()
    {
        HasEnded = true;
        State = MusicPlaybackState.Stopped;
        PlaybackEnded?.Invoke(this, new AudioPlaybackEndedEventArgs(Generation));
    }

    /// <summary>用指定代次触发播完事件，用于验证迟到事件被忽略。</summary>
    /// <param name="generation">事件代次。</param>
    public void RaiseEnded(int generation)
        => PlaybackEnded?.Invoke(this, new AudioPlaybackEndedEventArgs(generation));

    /// <summary>把引擎标记为已结束但不触发事件，用于模拟暂停期间的连接中断。</summary>
    public void MarkEnded()
    {
        HasEnded = true;
        State = MusicPlaybackState.Stopped;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Disposed = true;
        HasStream = false;
        HasEnded = false;
        State = MusicPlaybackState.Idle;
        Generation++;
        PlaybackEnded = null;
    }

    /// <summary>一次建流记录。</summary>
    /// <param name="Url">传给 BASS 的完整请求字符串。</param>
    /// <param name="OffsetBytes">起始字节偏移。</param>
    /// <param name="BasePosition">位置基准。</param>
    public sealed record LoadCall(string Url, long OffsetBytes, TimeSpan BasePosition);
}
