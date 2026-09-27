using Avalonia;
using Avalonia.Controls;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 把被测视图挂进一个窗口里，并跑一次布局。
/// </summary>
/// <remarks>
/// 刻意不在这里补 SukiUI 主题：主题由 <c>App.axaml</c> 的 <c>Application.Styles</c> 提供，
/// 测试宿主也照生产的方式走，才能验证"应用级主题确实够用"
/// （<c>BusyArea</c> 的加载动画要求 <c>SukiTheme</c> 必须在 <c>Application.Styles</c> 里）。
/// </remarks>
internal static class HeadlessViewHost
{
    /// <summary>挂载视图并完成一次测量/排布，返回承载它的窗口。</summary>
    /// <param name="view">被测视图。</param>
    /// <param name="width">窗口宽。</param>
    /// <param name="height">窗口高。</param>
    internal static async Task<Window> MountAsync(Control view, double width = 360, double height = 480)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
        };

        window.Show();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));

        // 绑定与模板应用发生在布局之后的消息循环里，让出一轮再断言。
        await Task.Yield();

        return window;
    }
}
