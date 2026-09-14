using QRCoder;

namespace BiliMusicDotNet.Tools.LoginQrProbe;

/// <summary>
/// 用 QRCoder 生成二维码并在终端渲染（不自行实现编码）。
/// </summary>
/// <remarks>
/// <para>
/// 只使用 QRCoder 的 <see cref="ASCIIQRCode"/> 渲染器：输出字符画，
/// 不依赖 System.Drawing，因此在各平台与无图形环境都能跑。
/// </para>
/// <para>
/// 采用 <c>GetGraphicSmall</c> 的默认配色（深色模块用空格、浅色模块用实心块）：
/// 二维码需要深色印在浅色底上，而终端多半是浅字深底，所以这个"看起来反色"的输出恰好是对的。
/// 输出的长宽比也接近 1:1，不会像逐模块横排那样被拉长。
/// </para>
/// </remarks>
internal static class QrConsoleRenderer
{
    /// <summary>渲染选项。</summary>
    /// <param name="QuietZone">是否绘制静区（规范要求四周各留 4 个模块，务必保持开启）。</param>
    internal readonly record struct Options(bool QuietZone);

    /// <summary>
    /// 把文本生成二维码并渲染成多行字符画。
    /// </summary>
    /// <param name="content">二维码内容。</param>
    /// <param name="options">渲染选项。</param>
    /// <returns>每行一个字符串（尚未加缩进）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> 为空。</exception>
    /// <exception cref="QRCoder.Exceptions.DataTooLongException">内容超出二维码容量。</exception>
    public static IReadOnlyList<string> Render(string content, Options options)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.L);
        using var renderer = new AsciiQRCode(data);

        var text = renderer.GetGraphicSmall(drawQuietZones: options.QuietZone);

        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
    }

    /// <summary>
    /// 直接打印到控制台（带缩进）。
    /// </summary>
    /// <param name="content">二维码内容。</param>
    /// <param name="options">渲染选项。</param>
    public static void WriteToConsole(string content, Options options)
    {
        foreach (var line in Render(content, options))
        {
            Console.WriteLine("   " + line);
        }
    }
}
