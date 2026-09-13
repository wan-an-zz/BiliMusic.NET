using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Logging;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 共享注册：日志、视图模型、共享视图与视图定位器。
    /// 平台主界面（<see cref="Abstracts.IMainViewService"/>）由各平台 head 自行注册。
    /// </summary>
    public static IServiceCollection AddBiliMusicDotNet(this IServiceCollection services)
    {
        // 日志：ILogger<T> 经 NLog 输出，级别过滤由 nlog.config 统一决定。
        services.AddAppLogging();
        
        services.AddSingleton<ViewLocator>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        // Views
        services.AddTransient<MainView>();
        services.AddTransient<MainWindow>();
        
        // Services
        services.AddSingleton<IBiliLoginService, BiliLoginService>();

        return services;
    }
}
