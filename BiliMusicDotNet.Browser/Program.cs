using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using BiliMusicDotNet;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Browser.Services;
using BiliMusicDotNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        // 容器必须在 Avalonia 启动前建好（页面刷新可能重入：容器只构建一次）。
        App.Services ??= BuildServices();

        return BuildAvaloniaApp()
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection()
            .AddBiliMusicDotNet()
            // 平台特定实现：浏览器用单视图形态的界面。
            .AddSingleton<IMainViewService, BrowserMainViewService>();

        // 构建期校验：注册缺失 / 生命周期错误在启动时立即暴露。
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
