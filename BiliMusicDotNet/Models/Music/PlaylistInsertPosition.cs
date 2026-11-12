namespace BiliMusicDotNet.Models.Music;

/// <summary>批量插入播放列表时的位置。</summary>
public enum PlaylistInsertPosition
{
    /// <summary>列表首位。</summary>
    Beginning,

    /// <summary>列表末尾。</summary>
    End,

    /// <summary>当前曲目之后；当前为空时退化为首位。</summary>
    AfterCurrent,
}
