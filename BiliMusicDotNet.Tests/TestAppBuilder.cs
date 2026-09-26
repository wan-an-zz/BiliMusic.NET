using Avalonia;
using Avalonia.Headless;
using BiliMusicDotNet.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace BiliMusicDotNet.Tests;

/// <summary>
/// Headless 测试宿主：用真实的 <see cref="BiliMusicDotNet.App"/> 起一个无窗口的 Avalonia 应用。
/// </summary>
/// <remarks>
/// 必须是真实 App 而不是裸 <see cref="Application"/>：样式、资源字典与 <c>avares://</c> 资产都挂在它身上。
/// <c>App.Services</c> 在所有容器取用之前就已设置好，因此测试里可以经它拿到被容器创建的视图。
/// </remarks>
public static class TestAppBuilder
{
    /// <summary>由 Avalonia.Headless.XUnit 调用，构建测试用的 AppBuilder。</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        // App 不认识具体界面：真实启动由各平台入口注入主视图服务，测试里只放一个空窗口宿主。
        BiliMusicDotNet.App.Services = HeadlessServices.Build();

        var options = new AvaloniaHeadlessPlatformOptions
        {
            // 必须关掉"假绘制"：开启时位图解码是桩实现，任何 PNG 的 PixelSize 都恒为 1x1，
            // 二维码与头像那几条断言会全部失真。关掉后走真实 Skia 光栅化。
            UseHeadlessDrawing = false,
        };

        return AppBuilder.Configure<BiliMusicDotNet.App>()
            .UseSkia()
            .UseHeadless(options);
    }
}
