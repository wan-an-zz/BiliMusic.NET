using BiliMusicDotNet.Models.Login;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 倒计时显示文本：格式化、取整方向与归零判定。纯函数，无需 Avalonia。
/// </summary>
public class CountdownFormatTests
{
    [Fact]
    public void 整分钟按分秒显示()
    {
        Assert.Equal("03:00", CountdownFormat.Remaining(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void 不足一秒仍显示一秒()
    {
        // 向上取整：读数不会因为显示截断而提前跳到 00:00，与"已过期"撞车。
        Assert.Equal("00:01", CountdownFormat.Remaining(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void 多出的不足一秒向上进位()
    {
        // 157.9 秒按 158 秒显示：宁可多报不到 1 秒，也不让倒计时比真实有效期更早归零。
        Assert.Equal("02:38", CountdownFormat.Remaining(TimeSpan.FromSeconds(157) + TimeSpan.FromMilliseconds(900)));
    }

    [Fact]
    public void 归零即过期()
    {
        Assert.True(CountdownFormat.HasExpired(TimeSpan.Zero));
        Assert.Equal(CountdownFormat.ExpiredText, CountdownFormat.Remaining(TimeSpan.Zero));
    }

    [Fact]
    public void 负剩余时间也算过期()
    {
        // 计时器与系统时钟之间存在偏差，剩余时间可能算出负数。
        Assert.True(CountdownFormat.HasExpired(TimeSpan.FromSeconds(-5)));
        Assert.Equal(CountdownFormat.ExpiredText, CountdownFormat.Remaining(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void 正剩余时间不算过期()
    {
        Assert.False(CountdownFormat.HasExpired(TimeSpan.FromSeconds(1)));
    }
}
