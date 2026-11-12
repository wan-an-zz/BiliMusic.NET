using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Music;
using Microsoft.Extensions.Logging;

namespace BiliMusicDotNet.Services;

/// <summary>
/// <see cref="IMusicService"/> 的默认实现：播放列表、地址解析、BASS 输出、预取与自动切歌都在这里收口。
/// </summary>
/// <remarks>
/// 公开操作由一把信号量串行化；<see cref="StateChanged"/> 在持锁期间触发，处理器不要同步回调本服务。
/// 播放地址只在内部解析，调用方通过播放方法间接使用。
/// </remarks>
public sealed class MusicService : IMusicService
{
    private const string ApiOrigin = "https://api.bilibili.com";
    private const string ContentReferer = "https://www.bilibili.com";
    private const string ViewPath = "/x/web-interface/view";
    private const string PlayUrlPath = "/x/player/playurl";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    private readonly IAudioEngine _audio;
    private readonly ICredentialService _credential;
    private readonly ILogger<MusicService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<MusicTrack> _playlist = [];
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<MusicStreamInfo>> _inflight = new(StringComparer.Ordinal);
    private readonly object _cacheGate = new();
    private readonly TimeSpan _cacheTtl;

    private MusicTrack[] _playlistSnapshot = [];
    private MusicTrack? _current;
    private MusicPlaybackState _state = MusicPlaybackState.Idle;
    private bool _hasPausedSnapshot;
    private TimeSpan _pausedPosition;
    private long _pausedBytes;
    private CancellationTokenSource? _prefetchCts;
    private string? _prefetchBvid;
    private bool _disposed;

    /// <summary>由容器创建：默认 HTTP 传输与 BASS 引擎。</summary>
    /// <param name="credentialService">凭据服务；播放地址请求会按需附加 Cookie。</param>
    /// <param name="logger">日志器。</param>
    public MusicService(ICredentialService credentialService, ILogger<MusicService> logger)
        : this(
            CreateDefaultHandler(),
            new BassAudioEngine(logger: logger),
            credentialService,
            logger,
            TimeSpan.FromMinutes(5))
    {
    }

    /// <summary>完整构造：显式注入 HTTP 传输与音频引擎，供测试与探针替换。</summary>
    /// <param name="handler">HTTP 传输，由本服务接管并在释放时销毁。</param>
    /// <param name="audio">音频引擎。</param>
    /// <param name="credentialService">凭据服务。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="cacheTtl">播放地址缓存时长；为零表示不缓存。</param>
    internal MusicService(
        HttpMessageHandler handler,
        IAudioEngine audio,
        ICredentialService credentialService,
        ILogger<MusicService> logger,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _credential = credentialService ?? throw new ArgumentNullException(nameof(credentialService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        _audio.PlaybackEnded += OnPlaybackEnded;
    }

    /// <inheritdoc />
    public IReadOnlyList<MusicTrack> Playlist => Volatile.Read(ref _playlistSnapshot);

    /// <inheritdoc />
    public MusicTrack? Current => _current;

    /// <inheritdoc />
    public MusicPlaybackState State => _state;

    /// <inheritdoc />
    public TimeSpan Position => _current is null ? TimeSpan.Zero : _audio.Position;

    /// <inheritdoc />
    public TimeSpan Duration
    {
        get
        {
            if (_current is null)
            {
                return TimeSpan.Zero;
            }

            return _current.Duration is { } duration && duration > TimeSpan.Zero
                ? duration
                : _audio.Duration;
        }
    }

    /// <inheritdoc />
    public event EventHandler<MusicStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<MusicPlaybackFailedEventArgs>? PlaybackFailed;

    /// <summary>当前预取目标的 BV 号；没有预取时为 <see langword="null"/>。</summary>
    internal string? PrefetchBvid => _prefetchBvid;

    /// <summary>是否存在尚未完成的预取。</summary>
    internal bool HasPendingPrefetch => _prefetchCts is not null;

    /// <inheritdoc />
    public async Task InsertMusicsAsync(
        IEnumerable<string> bvs,
        bool isBeginning = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var items = NormalizeBvs(bvs);
        if (items.Length == 0)
        {
            return;
        }

        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        foreach (var bvid in items)
        {
            _playlist.RemoveAll(track => string.Equals(track.Bvid, bvid, StringComparison.Ordinal));
        }

        var index = isBeginning ? 0 : _playlist.Count;
        _playlist.InsertRange(index, items.Select(bvid => new MusicTrack(bvid)));
        RefreshCurrentReference();
        PublishState();

        if (_current is null && _playlist.Count > 0)
        {
            await TryPlayCoreAsync(_playlist[0], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            SchedulePrefetch();
        }
    }

    /// <inheritdoc />
    public async Task ReplacePlaylistAsync(
        IEnumerable<string> bvs,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var items = NormalizeBvs(bvs);
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        CancelPrefetch();
        _audio.Unload();
        _current = null;
        _state = MusicPlaybackState.Idle;
        _hasPausedSnapshot = false;
        _pausedPosition = TimeSpan.Zero;
        _pausedBytes = 0;

        _playlist.Clear();
        _playlist.AddRange(items.Select(bvid => new MusicTrack(bvid)));
        PublishState();

        if (_playlist.Count > 0)
        {
            await TryPlayCoreAsync(_playlist[0], cancellationToken).ConfigureAwait(false);
            return;
        }

        SchedulePrefetch();
    }

    /// <inheritdoc />
    public async Task<bool> SelectMusicAsync(
        string bvid,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBv(bvid);
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        var track = FindTrack(normalized);
        return track is not null && await TrySelectCoreAsync(track, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        return _state == MusicPlaybackState.Playing
            ? PauseCore()
            : await TryResumeCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveMusicsAsync(
        IEnumerable<string> bvs,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var items = NormalizeBvs(bvs);
        if (items.Length == 0)
        {
            return;
        }

        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        if (_current is not null && items.Contains(_current.Bvid, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("不能删除当前正在播放的曲目。");
        }

        var removed = _playlist.RemoveAll(track => items.Contains(track.Bvid, StringComparer.Ordinal));
        if (removed > 0)
        {
            RefreshCurrentReference();
            PublishState();
        }

        SchedulePrefetch();
    }

    /// <inheritdoc />
    public async Task<bool> PauseAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return PauseCore();
    }

    /// <inheritdoc />
    public async Task<bool> ResumeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await TryResumeCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> PlayNextAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await PlayNextCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> PlayPreviousAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        if (_playlist.Count == 0)
        {
            return false;
        }

        if (_current is null)
        {
            return await TryPlayCoreAsync(_playlist[0], cancellationToken).ConfigureAwait(false);
        }

        var index = IndexOf(_current.Bvid);
        if (index <= 0)
        {
            return false;
        }

        return await TryPlayCoreAsync(_playlist[index - 1], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> PlayPlaylistMusicAsync(
        string bvid,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBv(bvid);
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        var index = IndexOf(normalized);
        if (index < 0)
        {
            throw new ArgumentException($"播放列表中没有 {normalized}。", nameof(bvid));
        }

        if (IsNextTrack(normalized))
        {
            var played = await PlayNextCoreAsync(cancellationToken).ConfigureAwait(false);
            if (played)
            {
                MoveToFirst(normalized);
                RefreshCurrentReference();
                PublishState();
                SchedulePrefetch();
            }

            return played;
        }

        var track = _playlist[index];
        MoveToFirst(normalized);
        PublishState();

        if (_current is not null && string.Equals(_current.Bvid, normalized, StringComparison.Ordinal))
        {
            return _state == MusicPlaybackState.Playing
                || await TryResumeCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        return await TryPlayCoreAsync(track, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> PlayMusicAsync(
        string bvid,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBv(bvid);
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        if (FindTrack(normalized) is null)
        {
            _playlist.Insert(0, new MusicTrack(normalized));
            PublishState();
            return await TryPlayCoreAsync(_playlist[0], cancellationToken).ConfigureAwait(false);
        }

        MoveToFirst(normalized);
        RefreshCurrentReference();
        PublishState();
        SchedulePrefetch();

        if (_current is not null && string.Equals(_current.Bvid, normalized, StringComparison.Ordinal))
        {
            return _state == MusicPlaybackState.Playing
                || await TryResumeCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        return await TryPlayCoreAsync(_playlist[0], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> InsertAndPlayAsync(
        IEnumerable<string> bvs,
        PlaylistInsertPosition position,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var items = NormalizeBvs(bvs);
        using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);

        if (items.Length == 0)
        {
            return false;
        }

        var insertIndex = position switch
        {
            PlaylistInsertPosition.Beginning => 0,
            PlaylistInsertPosition.End => _playlist.Count,
            PlaylistInsertPosition.AfterCurrent => _current is null ? 0 : IndexOf(_current.Bvid) + 1,
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };

        var removedBeforeInsert = 0;
        for (var i = 0; i < insertIndex && i < _playlist.Count; i++)
        {
            if (items.Contains(_playlist[i].Bvid, StringComparer.Ordinal))
            {
                removedBeforeInsert++;
            }
        }

        _playlist.RemoveAll(track => items.Contains(track.Bvid, StringComparer.Ordinal));
        insertIndex = Math.Clamp(insertIndex - removedBeforeInsert, 0, _playlist.Count);
        _playlist.InsertRange(insertIndex, items.Select(bvid => new MusicTrack(bvid)));
        RefreshCurrentReference();
        PublishState();

        var first = FindTrack(items[0]);
        if (first is null)
        {
            return false;
        }

        if (_current is not null && string.Equals(_current.Bvid, first.Bvid, StringComparison.Ordinal))
        {
            return _state == MusicPlaybackState.Playing
                || await TryResumeCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        return await TryPlayCoreAsync(first, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解析播放地址；仅供服务内部、测试与探针诊断使用。</summary>
    /// <param name="bvid">BV 号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>地址与元数据。</returns>
    internal async Task<MusicStreamInfo> GetPlaybackUrlAsync(
        string bvid,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeBv(bvid);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        Task<MusicStreamInfo> fetch;
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(normalized, out var cached))
            {
                if (cached.ExpiresAt > now)
                {
                    return cached.Info;
                }

                _cache.Remove(normalized);
            }

            if (!_inflight.TryGetValue(normalized, out fetch!))
            {
                fetch = FetchStreamInfoAsync(normalized, CancellationToken.None);
                _inflight[normalized] = fetch;
            }
        }

        try
        {
            var info = await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_cacheTtl > TimeSpan.Zero)
            {
                lock (_cacheGate)
                {
                    _cache[normalized] = new CacheEntry(info, DateTimeOffset.UtcNow + _cacheTtl);
                }
            }

            return info;
        }
        finally
        {
            lock (_cacheGate)
            {
                if (_inflight.TryGetValue(normalized, out var tracked) && ReferenceEquals(tracked, fetch))
                {
                    _inflight.Remove(normalized);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPrefetch();
        _audio.PlaybackEnded -= OnPlaybackEnded;

        if (_gate.Wait(TimeSpan.FromSeconds(5)))
        {
            try
            {
                _current = null;
                _state = MusicPlaybackState.Idle;
                _playlist.Clear();
                RefreshSnapshot();
                _audio.Unload();
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            _logger.LogWarning("释放 MusicService 时等待操作锁超时。");
        }

        _audio.Dispose();
        _http.Dispose();
    }

    private static HttpClientHandler CreateDefaultHandler()
        => new()
        {
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                | System.Net.DecompressionMethods.Deflate
                | System.Net.DecompressionMethods.Brotli,
        };

    private static string[] NormalizeBvs(IEnumerable<string> bvs)
    {
        ArgumentNullException.ThrowIfNull(bvs);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in bvs)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("BV 号不能为空。", nameof(bvs));
            }

            var bvid = raw.Trim();
            if (seen.Add(bvid))
            {
                result.Add(bvid);
            }
        }

        return result.ToArray();
    }

    private static string NormalizeBv(string bvid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bvid);
        return bvid.Trim();
    }

    private async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(MusicService));
        }

        return new GateLease(_gate);
    }

    private async Task<bool> TrySelectCoreAsync(MusicTrack track, CancellationToken cancellationToken)
    {
        var previousState = _state;
        var previousCurrent = _current;
        _state = MusicPlaybackState.Loading;
        PublishState();

        try
        {
            await SelectCoreAsync(track, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _state = previousCurrent is null ? MusicPlaybackState.Idle : previousState;
            PublishState();
            throw;
        }
        catch (Exception ex)
        {
            _state = previousCurrent is null ? MusicPlaybackState.Failed : previousState;
            _current = previousCurrent;
            RaisePlaybackFailed(track.Bvid, ex);
            PublishState(ex.Message);
            return false;
        }
    }

    private async Task SelectCoreAsync(MusicTrack track, CancellationToken cancellationToken)
    {
        var info = await GetPlaybackUrlAsync(track.Bvid, cancellationToken).ConfigureAwait(false);
        _audio.Load(info.Url.ToString());
        _current = UpsertTrack(info.Track);
        _hasPausedSnapshot = false;
        _state = MusicPlaybackState.Paused;
        PublishState();
        SchedulePrefetch();
    }

    private async Task<bool> TryPlayCoreAsync(MusicTrack track, CancellationToken cancellationToken)
    {
        if (!await TrySelectCoreAsync(track, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await TryResumeCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryResumeCoreAsync(CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            return false;
        }

        var bvid = _current.Bvid;
        try
        {
            if (!_audio.HasStream)
            {
                await SelectCoreAsync(_current, cancellationToken).ConfigureAwait(false);
                _audio.Play(false);
            }
            else if (_audio.HasEnded)
            {
                await ReloadCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _audio.Play(false);
            }

            _state = MusicPlaybackState.Playing;
            _hasPausedSnapshot = false;
            PublishState();
            SchedulePrefetch();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state = MusicPlaybackState.Failed;
            RaisePlaybackFailed(bvid, ex);
            PublishState(ex.Message);
            return false;
        }
    }

    private async Task ReloadCurrentAsync(CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            return;
        }

        var info = await GetPlaybackUrlAsync(_current.Bvid, cancellationToken).ConfigureAwait(false);
        var offset = _hasPausedSnapshot ? _pausedBytes : 0;
        var position = _hasPausedSnapshot && _pausedBytes > 0 ? _pausedPosition : TimeSpan.Zero;
        var url = info.Url.ToString();

        try
        {
            _audio.Load(url, offset, position);
        }
        catch (Exception ex) when (offset > 0)
        {
            _logger.LogWarning(
                ex,
                "从 {Bytes} 字节恢复 {Bvid} 失败，改为从头播放。",
                offset,
                _current.Bvid);
            _audio.Load(url, 0, TimeSpan.Zero);
        }

        _audio.Play(false);
    }

    private bool PauseCore()
    {
        if (_current is null)
        {
            return false;
        }

        if (_state == MusicPlaybackState.Paused)
        {
            return true;
        }

        if (_state != MusicPlaybackState.Playing)
        {
            return false;
        }

        try
        {
            _audio.Pause();
        }
        catch (Exception ex)
        {
            _state = MusicPlaybackState.Failed;
            RaisePlaybackFailed(_current.Bvid, ex);
            PublishState(ex.Message);
            return false;
        }

        _pausedBytes = _audio.PositionBytes;
        _pausedPosition = _audio.Position;
        _hasPausedSnapshot = true;
        _state = MusicPlaybackState.Paused;
        PublishState();
        return true;
    }

    private async Task<bool> PlayNextCoreAsync(CancellationToken cancellationToken)
    {
        if (_playlist.Count == 0)
        {
            return false;
        }

        if (_current is null)
        {
            return await TryPlayCoreAsync(_playlist[0], cancellationToken).ConfigureAwait(false);
        }

        var index = IndexOf(_current.Bvid);
        if (index < 0 || index + 1 >= _playlist.Count)
        {
            return false;
        }

        return await TryPlayCoreAsync(_playlist[index + 1], cancellationToken).ConfigureAwait(false);
    }

    private bool IsNextTrack(string bvid)
    {
        if (_current is null)
        {
            return false;
        }

        var index = IndexOf(_current.Bvid);
        return index >= 0
            && index + 1 < _playlist.Count
            && string.Equals(_playlist[index + 1].Bvid, bvid, StringComparison.Ordinal);
    }

    private void MoveToFirst(string bvid)
    {
        var index = IndexOf(bvid);
        if (index <= 0)
        {
            return;
        }

        var track = _playlist[index];
        _playlist.RemoveAt(index);
        _playlist.Insert(0, track);
    }

    private void RefreshCurrentReference()
    {
        if (_current is not null)
        {
            _current = FindTrack(_current.Bvid) ?? _current;
        }
    }

    private MusicTrack[] RefreshSnapshot()
    {
        var snapshot = _playlist.ToArray();
        Volatile.Write(ref _playlistSnapshot, snapshot);
        return snapshot;
    }

    private MusicTrack? FindTrack(string bvid)
    {
        foreach (var track in _playlist)
        {
            if (string.Equals(track.Bvid, bvid, StringComparison.Ordinal))
            {
                return track;
            }
        }

        return null;
    }

    private int IndexOf(string bvid)
    {
        for (var i = 0; i < _playlist.Count; i++)
        {
            if (string.Equals(_playlist[i].Bvid, bvid, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private MusicTrack UpsertTrack(MusicTrack info)
    {
        var index = IndexOf(info.Bvid);
        if (index < 0)
        {
            return info;
        }

        var existing = _playlist[index];
        var merged = existing with
        {
            Title = info.Title ?? existing.Title,
            OwnerName = info.OwnerName ?? existing.OwnerName,
            Duration = info.Duration ?? existing.Duration,
        };
        _playlist[index] = merged;
        return merged;
    }

    private void PublishState(string? message = null)
    {
        var snapshot = RefreshSnapshot();
        var handler = StateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new MusicStateChangedEventArgs(snapshot, _current, _state, message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StateChanged 事件处理器抛出异常。");
        }
    }

    private void RaisePlaybackFailed(string bvid, Exception exception)
    {
        var handler = PlaybackFailed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new MusicPlaybackFailedEventArgs(bvid, exception.Message, exception));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaybackFailed 事件处理器抛出异常。");
        }
    }

    private void OnPlaybackEnded(object? sender, AudioPlaybackEndedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(() => HandlePlaybackEndedAsync(e.Generation));
    }

    /// <summary>处理一次自然播完；测试可直接调用以避开线程调度。</summary>
    /// <param name="generation">事件携带的流代次。</param>
    internal async Task HandlePlaybackEndedAsync(int generation)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed
                || generation != _audio.Generation
                || _state != MusicPlaybackState.Playing
                || _current is null)
            {
                return;
            }

            var index = IndexOf(_current.Bvid);
            if (index < 0)
            {
                return;
            }

            if (index + 1 >= _playlist.Count)
            {
                _state = MusicPlaybackState.Stopped;
                _hasPausedSnapshot = false;
                CancelPrefetch();
                PublishState();
                return;
            }

            _state = MusicPlaybackState.Stopped;
            await TryPlayCoreAsync(_playlist[index + 1], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动切歌失败。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SchedulePrefetch()
    {
        CancelPrefetch();
        if (_disposed || _current is null)
        {
            return;
        }

        var index = IndexOf(_current.Bvid);
        if (index < 0 || index + 1 >= _playlist.Count)
        {
            return;
        }

        var next = _playlist[index + 1];
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        _prefetchBvid = next.Bvid;
        _ = PrefetchAsync(next.Bvid, cts);
    }

    private void CancelPrefetch()
    {
        var cts = _prefetchCts;
        _prefetchCts = null;
        _prefetchBvid = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task PrefetchAsync(string bvid, CancellationTokenSource cts)
    {
        try
        {
            var info = await GetPlaybackUrlAsync(bvid, cts.Token).ConfigureAwait(false);
            await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || _current is null || !IsNextTrack(bvid))
                {
                    return;
                }

                var before = FindTrack(bvid);
                var updated = UpsertTrack(info.Track);
                if (!Equals(before, updated))
                {
                    PublishState();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "预取 {Bvid} 失败。", bvid);
        }
        finally
        {
            if (ReferenceEquals(_prefetchCts, cts))
            {
                _prefetchCts = null;
                _prefetchBvid = null;
            }

            cts.Dispose();
        }
    }

    private async Task<MusicStreamInfo> FetchStreamInfoAsync(string bvid, CancellationToken cancellationToken)
    {
        var view = await GetViewAsync(bvid, cancellationToken).ConfigureAwait(false);
        var candidates = await GetPlayUrlAsync(bvid, view.Cid, cancellationToken).ConfigureAwait(false);
        return BuildStreamInfo(bvid, view, candidates);
    }

    private async Task<ViewInfo> GetViewAsync(string bvid, CancellationToken cancellationToken)
    {
        var query = "bvid=" + Uri.EscapeDataString(bvid);
        var data = await SendApiAsync(ViewPath, query, cancellationToken).ConfigureAwait(false);
        var cid = GetInt64(data, "cid");
        if (cid <= 0)
        {
            throw new BiliApiException(-1, "view 响应缺少 cid", ViewPath);
        }

        var owner = data.TryGetProperty("owner", out var ownerElement)
            ? GetString(ownerElement, "name")
            : null;
        var duration = GetDouble(data, "duration");

        return new ViewInfo(
            cid,
            GetString(data, "title"),
            owner,
            duration > 0 ? TimeSpan.FromSeconds(duration) : null);
    }

    private async Task<ProgressivePlayUrl> GetPlayUrlAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken)
    {
        // BASS 的 URL 流无法解析 DASH 的分片 m4s，改用可顺序读取的 durl MP4；
        // qn=80 在匿名状态下会降级到 720P，其音轨约 212 kbps。
        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"bvid={Uri.EscapeDataString(bvid)}&cid={cid}&qn=80&fnver=0&fnval=1&fourk=1");
        var data = await SendApiAsync(PlayUrlPath, query, cancellationToken).ConfigureAwait(false);

        if (!data.TryGetProperty("durl", out var durl)
            || durl.ValueKind != JsonValueKind.Array
            || durl.GetArrayLength() == 0)
        {
            throw new BiliApiException(-1, "playurl 响应没有可用的音频流", PlayUrlPath);
        }

        var segments = new List<ProgressiveSegment>();
        foreach (var item in durl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var backups = new List<string>();
            if ((item.TryGetProperty("backup_url", out var backupElement)
                 || item.TryGetProperty("backupUrl", out backupElement))
                && backupElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var backup in backupElement.EnumerateArray())
                {
                    if (backup.ValueKind == JsonValueKind.String && backup.GetString() is { } value)
                    {
                        backups.Add(value);
                    }
                }
            }

            var order = GetInt64(item, "order");
            order = Math.Clamp(order, 0, int.MaxValue);
            segments.Add(new ProgressiveSegment(
                GetString(item, "url") ?? GetString(item, "base_url"),
                backups,
                (int)order));
        }

        if (segments.Count == 0)
        {
            throw new BiliApiException(-1, "playurl 响应没有可用的音频流", PlayUrlPath);
        }

        var quality = GetInt64(data, "quality");
        quality = Math.Clamp(quality, 0, int.MaxValue);
        return new ProgressivePlayUrl(
            (int)quality,
            GetString(data, "format"),
            segments);
    }

    private async Task<JsonElement> SendApiAsync(
        string path,
        string query,
        CancellationToken cancellationToken)
    {
        var url = ApiOrigin + path + "?" + query;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Referer", ContentReferer);
        request.Headers.TryAddWithoutValidation("Origin", ContentReferer);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var cookie = _credential.Current?.CookieHeader;
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new BiliApiException(
                (int)response.StatusCode,
                response.ReasonPhrase ?? "HTTP 请求失败",
                path);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new MusicPlaybackException($"解析 {path} 响应失败。", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new MusicPlaybackException($"解析 {path} 响应失败。");
            }

            var code = GetInt32(root, "code");
            if (code != 0)
            {
                throw new BiliApiException(code, GetString(root, "message") ?? "未知错误", path);
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            {
                throw new BiliApiException(-1, "响应缺少 data 字段", path);
            }

            return data.Clone();
        }
    }

    private static MusicStreamInfo BuildStreamInfo(
        string bvid,
        ViewInfo view,
        ProgressivePlayUrl playUrl)
    {
        var segment = playUrl.Segments
            .OrderBy(item => item.Order)
            .Select(item => (Segment: item, Urls: NormalizeUrls(item)))
            .FirstOrDefault(item => item.Urls.Count > 0);

        if (segment.Urls is null || segment.Urls.Count == 0)
        {
            throw new BiliApiException(-1, "音频流地址全部无效", PlayUrlPath);
        }

        var track = new MusicTrack(
            bvid,
            NullIfWhiteSpace(view.Title),
            NullIfWhiteSpace(view.Owner),
            view.Duration);

        return new MusicStreamInfo(
            track,
            segment.Urls[0],
            segment.Urls.Skip(1).ToArray(),
            playUrl.Quality,
            NullIfWhiteSpace(playUrl.Format));
    }

    private static List<Uri> NormalizeUrls(ProgressiveSegment segment)
    {
        var result = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? raw)
        {
            var normalized = NormalizeUrl(raw);
            if (normalized is not null && seen.Add(normalized.ToString()))
            {
                result.Add(normalized);
            }
        }

        Add(segment.Url);
        foreach (var backup in segment.BackupUrls)
        {
            Add(backup);
        }

        return result;
    }

    private static Uri? NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value[7..];
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetInt64(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static int GetInt32(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static double GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetDouble(out var result)
            ? result
            : 0;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }

    private sealed record CacheEntry(MusicStreamInfo Info, DateTimeOffset ExpiresAt);

    private sealed record ViewInfo(long Cid, string? Title, string? Owner, TimeSpan? Duration);

    private sealed record ProgressivePlayUrl(
        int Quality,
        string? Format,
        List<ProgressiveSegment> Segments);

    private sealed record ProgressiveSegment(
        string? Url,
        List<string> BackupUrls,
        int Order);
}
