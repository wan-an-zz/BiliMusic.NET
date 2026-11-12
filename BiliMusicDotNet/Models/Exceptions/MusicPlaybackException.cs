using System;

namespace BiliMusicDotNet.Models.Exceptions;

/// <summary>BASS 初始化、建流或播放控制失败时抛出。</summary>
internal sealed class MusicPlaybackException(string message, Exception? innerException = null)
    : Exception(message, innerException);
