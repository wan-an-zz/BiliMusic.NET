using System;
using Avalonia;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.DependencyInjection;
using BiliMusicDotNet.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 容器必须在 Avalonia 启动前建好：App 只负责从容器取出主视图并挂载。
        App.Services = ConfigureServices();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont();

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection()
            .AddBiliMusicDotNet()
            // 平台特定实现：桌面端用窗口形态的主界面。
            .AddSingleton<IMainViewService, DesktopMainViewService>();

        // 构建期校验：注册缺失 / 生命周期错误在启动时立即暴露。
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
