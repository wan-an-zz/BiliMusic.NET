using System;

namespace BiliMusicDotNet.Models.Music;

/// <summary>
/// 播放列表中的一首音乐；<see cref="Bvid"/> 是唯一标识，其余字段在解析成功后回填。
/// </summary>
/// <param name="Bvid">B 站视频 BV 号。</param>
/// <param name="Title">标题；尚未解析时为 <see langword="null"/>。</param>
/// <param name="OwnerName">UP 主昵称；尚未解析时为 <see langword="null"/>。</param>
/// <param name="Duration">时长；尚未解析时为 <see langword="null"/>。</param>
public sealed record MusicTrack(
    string Bvid,
    string? Title = null,
    string? OwnerName = null,
    TimeSpan? Duration = null);
