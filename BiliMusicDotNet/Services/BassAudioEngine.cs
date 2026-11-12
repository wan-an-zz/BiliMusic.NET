using System;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Music;
using ManagedBass;
using Microsoft.Extensions.Logging;

namespace BiliMusicDotNet.Services;

/// <summary>
/// 基于 BASS 的输出实现：<c>BASS_StreamCreateURL</c> 直接播放网络流，配合分块缓冲降低内存占用。
/// </summary>
internal sealed class BassAudioEngine : IAudioEngine
{
    /// <summary>内容接口要求的 Referer。</summary>
    internal const string Referer = "https://www.bilibili.com";

    /// <summary>与登录服务一致的现代桌面浏览器 UA。</summary>
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    private readonly string? _proxy;
    private readonly ILogger? _logger;

    private int _handle;
    private int _generation;
    private int _endSync;
    private bool _initialized;
    private bool _disposed;
    private bool _hasEnded;
    private bool _stopped;
    private long _baseOffsetBytes;
    private TimeSpan _basePosition;
    private SyncProcedure? _endCallback;

    /// <summary>创建引擎。</summary>
    /// <param name="proxy">可选的 BASS 代理地址；为空时使用系统默认。</param>
    /// <param name="logger">可选日志器，用于记录同步器注册失败这类非致命问题。</param>
    public BassAudioEngine(string? proxy = null, ILogger? logger = null)
    {
        _proxy = proxy;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool HasStream => _handle != 0;

    /// <inheritdoc />
    public bool HasEnded => _hasEnded;

    /// <inheritdoc />
    public int Generation => _generation;

    /// <inheritdoc />
    public MusicPlaybackState State
    {
        get
        {
            if (_handle == 0)
            {
                return MusicPlaybackState.Idle;
            }

            if (_hasEnded || _stopped)
            {
                return MusicPlaybackState.Stopped;
            }

            return Bass.ChannelIsActive(_handle) switch
            {
                PlaybackState.Playing or PlaybackState.Stalled => MusicPlaybackState.Playing,
                PlaybackState.Paused => MusicPlaybackState.Paused,
                _ => MusicPlaybackState.Paused,
            };
        }
    }

    /// <inheritdoc />
    public TimeSpan Position
    {
        get
        {
            if (_handle == 0)
            {
                return _basePosition;
            }

            var bytes = Bass.ChannelGetPosition(_handle, PositionFlags.Bytes);
            if (bytes <= 0)
            {
                return _basePosition;
            }

            var seconds = Bass.ChannelBytes2Seconds(_handle, bytes);
            return seconds <= 0 ? _basePosition : _basePosition + TimeSpan.FromSeconds(seconds);
        }
    }

    /// <inheritdoc />
    public long PositionBytes
    {
        get
        {
            if (_handle == 0)
            {
                return _baseOffsetBytes;
            }

            var bytes = Bass.ChannelGetPosition(_handle, PositionFlags.Bytes);
            return bytes <= 0 ? _baseOffsetBytes : _baseOffsetBytes + bytes;
        }
    }

    /// <inheritdoc />
    public TimeSpan Duration
    {
        get
        {
            if (_handle == 0)
            {
                return TimeSpan.Zero;
            }

            var bytes = Bass.ChannelGetLength(_handle, PositionFlags.Bytes);
            if (bytes <= 0)
            {
                return TimeSpan.Zero;
            }

            var seconds = Bass.ChannelBytes2Seconds(_handle, bytes);
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }
    }

    /// <inheritdoc />
    public event EventHandler<AudioPlaybackEndedEventArgs>? PlaybackEnded;

    /// <inheritdoc />
    public void Initialize()
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        if (!Bass.Init())
        {
            throw new MusicPlaybackException(
                $"BASS 初始化失败（{Bass.LastError}）。请确认原生库已随程序部署，且系统存在可用的音频输出设备。");
        }

        Bass.NetTimeOut = 8000;
        Bass.NetReadTimeOut = 15000;
        // NetPreBuffer 是百分比：预缓冲约占 5 秒下载缓冲的 20%，兼顾首播速度与抗卡顿。
        Bass.NetPreBuffer = 20;
        if (!string.IsNullOrWhiteSpace(_proxy))
        {
            Bass.NetProxy = _proxy;
        }

        _initialized = true;
    }

    /// <inheritdoc />
    public long Load(string url, long offsetBytes = 0, TimeSpan basePosition = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Initialize();

        var offset = offsetBytes switch
        {
            <= 0 => 0,
            > int.MaxValue => int.MaxValue,
            _ => (int)offsetBytes,
        };

        var request = BuildUrlRequest(url);
        var newHandle = Bass.CreateStream(request, offset, BassFlags.StreamDownloadBlocks, null);
        if (newHandle == 0)
        {
            throw new MusicPlaybackException($"创建音频流失败（{Bass.LastError}）。");
        }

        var oldHandle = _handle;
        _handle = newHandle;
        _baseOffsetBytes = offset;
        _basePosition = basePosition;
        _hasEnded = false;
        _stopped = false;
        _generation++;
        var generation = _generation;

        _endCallback = (handle, _, _, _) =>
        {
            if (handle != _handle)
            {
                return;
            }

            _hasEnded = true;
            PlaybackEnded?.Invoke(this, new AudioPlaybackEndedEventArgs(generation));
        };

        _endSync = Bass.ChannelSetSync(newHandle, SyncFlags.End, 0, _endCallback);
        if (_endSync == 0)
        {
            _logger?.LogWarning("注册 BASS 结束同步器失败：{Error}", Bass.LastError);
        }

        if (oldHandle != 0)
        {
            Bass.StreamFree(oldHandle);
        }

        return _generation;
    }

    /// <inheritdoc />
    public void Play(bool restart = false)
    {
        ThrowIfDisposed();
        if (_handle == 0)
        {
            throw new MusicPlaybackException("没有可播放的音频流。");
        }

        if (!Bass.ChannelPlay(_handle, restart))
        {
            throw new MusicPlaybackException($"开始播放失败（{Bass.LastError}）。");
        }

        _stopped = false;
    }

    /// <inheritdoc />
    public void Pause()
    {
        ThrowIfDisposed();
        if (_handle == 0)
        {
            throw new MusicPlaybackException("没有可暂停的音频流。");
        }

        if (!Bass.ChannelPause(_handle))
        {
            throw new MusicPlaybackException($"暂停失败（{Bass.LastError}）。");
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        ThrowIfDisposed();
        if (_handle == 0)
        {
            return;
        }

        if (!Bass.ChannelStop(_handle))
        {
            throw new MusicPlaybackException($"停止失败（{Bass.LastError}）。");
        }

        _stopped = true;
    }

    /// <inheritdoc />
    public void Unload()
    {
        if (_handle != 0)
        {
            Bass.StreamFree(_handle);
            _handle = 0;
        }

        _generation++;
        _hasEnded = false;
        _stopped = false;
        _baseOffsetBytes = 0;
        _basePosition = TimeSpan.Zero;
        _endSync = 0;
        _endCallback = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unload();
        PlaybackEnded = null;

        if (_initialized)
        {
            Bass.Free();
            _initialized = false;
        }
    }

    /// <summary>按 BASS 约定把自定义 HTTP 头接在 URL 后面，每行以 CRLF 结尾。</summary>
    /// <param name="url">音频地址。</param>
    /// <param name="referer">Referer 头。</param>
    /// <param name="userAgent">User-Agent 头。</param>
    /// <returns>可直接传给 <c>BASS_StreamCreateURL</c> 的请求字符串。</returns>
    internal static string BuildUrlRequest(
        string url,
        string referer = Referer,
        string userAgent = UserAgent)
        => url + "\r\n" +
           "Referer: " + referer + "\r\n" +
           "User-Agent: " + userAgent + "\r\n";

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
