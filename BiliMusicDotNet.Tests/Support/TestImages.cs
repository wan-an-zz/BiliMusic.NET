using QRCoder;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 测试用图片字节：拿 QRCoder 生成一张真 PNG，省得在用例里手工拼图像数据。
/// </summary>
internal static class TestImages
{
    private static readonly Lazy<byte[]> LazyPng = new(() =>
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode("https://example.com/test.png", QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data);

        return renderer.GetGraphic(4, drawQuietZones: false);
    });

    /// <summary>一张合法 PNG 的字节（可直接被 <c>Bitmap</c> 解码）。</summary>
    internal static byte[] Png => LazyPng.Value;
}
