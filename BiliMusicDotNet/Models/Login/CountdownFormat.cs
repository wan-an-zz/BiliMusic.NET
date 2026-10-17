using System;

namespace BiliMusicDotNet.Models.Login;

/// <summary>
/// 二维码剩余有效期的显示文本。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了让倒计时能被单测直接驱动：真实计时器在测试里不会自己走，
/// </remarks>
public static class CountdownFormat
{
    /// <summary>已过期的显示文本。</summary>
    public const string ExpiredText = "已过期";

    /// <summary>
    /// 判断剩余时间是否已经归零。
    /// </summary>
    /// <param name="remaining">距失效时刻的剩余时间。</param>
    /// <returns>剩余时间不为正时为 <see langword="true"/>。</returns>
    public static bool HasExpired(TimeSpan remaining) => remaining <= TimeSpan.Zero;

    /// <summary>
    /// 把剩余时间格式化成 <c>mm:ss</c>。
    /// </summary>
    /// <param name="remaining">距失效时刻的剩余时间。</param>
    /// <returns>
    /// 形如 <c>02:37</c> 的文本；剩余时间为正但不足 1 秒时给 <c>00:01</c>（不显示 00:00，
    /// 否则会和"已过期"在观感上撞车）；剩余时间不为正时返回 <see cref="ExpiredText"/>。
    /// </returns>
    public static string Remaining(TimeSpan remaining)
    {
        if (HasExpired(remaining))
        {
            return ExpiredText;
        }

        // 向上取整到秒：读数不会因为显示截断而提前跳到 00:00。
        var totalSeconds = (long)Math.Ceiling(remaining.TotalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;

        return $"{minutes:00}:{seconds:00}";
    }
}
