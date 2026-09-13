using BiliMusicDotNet.Logging;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.DependencyInjection;

/// <summary>
/// 应用服务注册入口（组合根的注册部分）。
/// </summary>
/// <remarks>
/// 生命周期约定：
/// <list type="bullet">
/// <item>
/// <description><see cref="MainViewModel"/> 为单例：应用外壳状态需要跨视图复用同一实例。</description>
/// </item>
/// <item>
/// <description><see cref="MainView"/>、<see cref="MainWindow"/> 为瞬态：每次请求都应得到新的可视树，避免控件被多处复用。</description>
/// </item>
/// <item>
/// <description><see cref="ViewLocator"/> 为单例：它是无状态的模板实现，会被 <c>Application.DataTemplates</c> 长期持有。</description>
/// </item>
/// </list>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册应用运行所需的日志、视图模型、视图与视图定位器。
    /// </summary>
    /// <param name="services">待填充的服务集合。</param>
    /// <returns>同一个 <paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddBiliMusicDotNet(this IServiceCollection services)
    {
        // 日志：ILogger<T> 经 NLog 输出，级别过滤由 nlog.config 统一决定。
        services.AddAppLogging();

        // 视图定位器：无状态，作为数据模板长期存活。
        services.AddSingleton<ViewLocator>();

        // 视图模型：应用外壳状态，需跨视图复用同一实例。
        services.AddSingleton<MainViewModel>();

        // 视图：每次请求都应是新的可视树（单视图平台可能按 Activity 反复创建）。
        services.AddTransient<MainView>();
        services.AddTransient<MainWindow>();

        return services;
    }
}
