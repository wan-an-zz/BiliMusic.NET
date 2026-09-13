using System;
using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.DependencyInjection;
using BiliMusicDotNet.iOS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // 平台入口可能被多次调用：容器只构建一次。
        App.Services ??= BuildServices();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection()
            .AddBiliMusicDotNet()
            // 平台特定实现：iOS 用单视图形态的主界面。
            .AddSingleton<IMainViewService, IosMainViewService>();

        // 构建期校验：注册缺失 / 生命周期错误在启动时立即暴露。
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
