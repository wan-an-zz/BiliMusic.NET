using System.Text;

namespace BiliMusicDotNet.Tools.LoginQrProbe;

/// <summary>
/// 把二维码矩阵渲染成终端可显示的字符画。
/// </summary>
internal static class QrConsoleRenderer
{
    /// <summary>渲染选项。</summary>
    /// <param name="UseBlocks">是否使用半块字符（更紧凑、更接近正方形）。</param>
    /// <param name="UseAnsiColors">是否使用 ANSI 反色（保证浅色终端也能扫）。</param>
    /// <param name="QuietZone">静区宽度（模块数），规范要求 4。</param>
    internal readonly record struct Options(bool UseBlocks, bool UseAnsiColors, int QuietZone);

    /// <summary>
    /// 渲染为多行文本。
    /// </summary>
    /// <param name="modules">模块矩阵。</param>
    /// <param name="options">渲染选项。</param>
    /// <returns>每行一个字符串。</returns>
    public static IReadOnlyList<string> Render(bool[,] modules, Options options)
    {
        var size = modules.GetLength(0);
        var q = Math.Max(0, options.QuietZone);
        var total = size + q * 2;

        // ANSI：把前景/背景反转后打印空格，终端配色的深浅都不影响扫码。
        const string darkOnLight = "\u001b[7m";   // 反显
        const string reset = "\u001b[0m";

        var lines = new List<string>();

        if (options.UseBlocks && options.UseAnsiColors)
        {
            // 一次输出两行：上半块 ▀ 与下半块 ▄ 组合，纵向压缩一半。
            // 只在能用 ANSI 反显时走这条路——否则浅色终端上会变成浅底浅块，扫不出来。
            for (var y = 0; y < total; y += 2)
            {
                var sb = new StringBuilder();
                for (var x = 0; x < total; x++)
                {
                    sb.Append(BlockChar(IsDark(modules, x - q, y - q), IsDark(modules, x - q, y + 1 - q)));
                }

                lines.Add(sb.ToString());
            }

            return lines;
        }

        // 逐模块输出：一个模块占一个字符单元（宽 2 字符保证接近正方形），不做纵向合并以免丢模块。
        for (var y = 0; y < total; y++)
        {
            var sb = new StringBuilder();
            for (var x = 0; x < total; x++)
            {
                var dark = IsDark(modules, x - q, y - q);

                if (options.UseAnsiColors)
                {
                    sb.Append(dark ? $"{darkOnLight}  {reset}" : "  ");
                }
                else
                {
                    sb.Append(dark ? "██" : "  ");
                }
            }

            lines.Add(sb.ToString());
        }

        return lines;
    }

    /// <summary>取模块是否深色；超出矩阵范围（静区）视为浅色。</summary>
    private static bool IsDark(bool[,] modules, int x, int y)
    {
        var size = modules.GetLength(0);
        return x >= 0 && y >= 0 && x < size && y < size && modules[y, x];
    }

    private static string BlockChar(bool upper, bool lower)
        => (upper, lower) switch
        {
            // 上下同色时用整块，保证横向宽度与其他情况一致（都是 1 个字符宽）。
            (true, true) => "█",
            (true, false) => "▀",
            (false, true) => "▄",
            (false, false) => " ",
        };
}
