using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliMusicDotNet.Models.Music;

namespace BiliMusicDotNet.Abstracts;

/// <summary>
/// 音乐播放的唯一入口：维护播放列表、解析播放地址、驱动音频输出并预取下一首。
/// </summary>
/// <remarks>
/// 所有公开操作串行执行；<see cref="StateChanged"/> 在后台线程触发，UI 需自行转投 UI 线程。
/// 播放地址由服务内部解析，调用方无需也不应手动获取。
/// </remarks>
public interface IMusicService : IDisposable
{
    /// <summary>当前播放列表快照。</summary>
    IReadOnlyList<MusicTrack> Playlist { get; }

    /// <summary>当前曲目；没有时为 <see langword="null"/>。</summary>
    MusicTrack? Current { get; }

    /// <summary>当前播放状态。</summary>
    MusicPlaybackState State { get; }

    /// <summary>当前播放位置。</summary>
    TimeSpan Position { get; }

    /// <summary>当前曲目总时长。</summary>
    TimeSpan Duration { get; }

    /// <summary>播放列表、当前曲目或播放状态变化时触发。</summary>
    event EventHandler<MusicStateChangedEventArgs>? StateChanged;

    /// <summary>用户可见的加载或播放失败时触发。</summary>
    event EventHandler<MusicPlaybackFailedEventArgs>? PlaybackFailed;

    /// <summary>把一批 BV 载入播放列表；已存在的 BV 会移动到目标位置，不产生重复项。</summary>
    /// <param name="bvs">BV 号集合。</param>
    /// <param name="isBeginning">为 <see langword="true"/> 时插入列表首，否则插入列表尾。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InsertMusicsAsync(
        IEnumerable<string> bvs,
        bool isBeginning = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止并释放当前流，清空列表后载入新列表；新列表非空时自动播放第一首。
    /// </summary>
    /// <param name="bvs">新播放列表的 BV 号集合，重复项按首次出现保留。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ReplacePlaylistAsync(
        IEnumerable<string> bvs,
        CancellationToken cancellationToken = default);

    /// <summary>把列表中的曲目设为当前曲目并准备好音频流，但不开始播放。</summary>
    /// <param name="bvid">目标 BV 号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否成功；BV 不在列表或加载失败时返回 <see langword="false"/>。</returns>
    Task<bool> SelectMusicAsync(
        string bvid,
        CancellationToken cancellationToken = default);

    /// <summary>播放中则暂停，否则继续播放。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否成功；没有当前曲目时返回 <see langword="false"/>。</returns>
    Task<bool> TogglePlayPauseAsync(CancellationToken cancellationToken = default);

    /// <summary>从列表删除一批 BV。</summary>
    /// <param name="bvs">BV 号集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">集合包含当前曲目；此时整批都不删除。</exception>
    Task RemoveMusicsAsync(
        IEnumerable<string> bvs,
        CancellationToken cancellationToken = default);

    /// <summary>暂停当前曲目。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否成功；没有当前曲目或当前不在播放时返回 <see langword="false"/>。</returns>
    Task<bool> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>继续播放当前曲目；音频流已结束时按保存的位置重建。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否成功；没有当前曲目时返回 <see langword="false"/>。</returns>
    Task<bool> ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>播放下一首；尾曲时返回 <see langword="false"/>，不循环。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> PlayNextAsync(CancellationToken cancellationToken = default);

    /// <summary>播放上一首；首曲时返回 <see langword="false"/>，不循环。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> PlayPreviousAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 播放列表中的指定曲目并把它移到第一位；若它是当前曲目的下一首，先切歌再移动，避免重复 Resume。
    /// </summary>
    /// <param name="bvid">目标 BV 号，必须已在列表中。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentException">BV 不在列表中。</exception>
    Task<bool> PlayPlaylistMusicAsync(string bvid, CancellationToken cancellationToken = default);

    /// <summary>播放任意 BV；不在列表时插入到首位，已在列表时移动到首位。</summary>
    /// <param name="bvid">目标 BV 号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> PlayMusicAsync(string bvid, CancellationToken cancellationToken = default);

    /// <summary>把一批 BV 插入指定位置并播放本批第一首。</summary>
    /// <param name="bvs">BV 号集合。</param>
    /// <param name="position">插入位置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> InsertAndPlayAsync(
        IEnumerable<string> bvs,
        PlaylistInsertPosition position,
        CancellationToken cancellationToken = default);
}
