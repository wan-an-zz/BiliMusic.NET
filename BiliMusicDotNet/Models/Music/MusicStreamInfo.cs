using System;
using System.Collections.Generic;

namespace BiliMusicDotNet.Models.Music;

/// <summary>解析完成的音频地址与元数据，内部使用。</summary>
internal sealed record MusicStreamInfo(
    MusicTrack Track,
    Uri Url,
    IReadOnlyList<Uri> BackupUrls,
    int Quality,
    string? Format);
