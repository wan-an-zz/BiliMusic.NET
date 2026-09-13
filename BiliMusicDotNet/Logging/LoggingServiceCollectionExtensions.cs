using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

namespace BiliMusicDotNet.Logging;

/// <summary>
/// 日志服务注册扩展：把 NLog 接入 <see cref="ILogger{TCategoryName}"/> 抽象。
/// </summary>
public static class LoggingServiceCollectionExtensions
{
    /// <summary>
    /// 注册日志服务，使 NLog 成为唯一输出管道。
    /// </summary>
    /// <remarks>
    /// 最低级别放宽到 Trace，真正的过滤统一交给 nlog.config 的规则，避免 MEL 与 NLog 两处规则互相掩盖；
    /// 未命中规则的事件会被提供程序直接短路，不产生格式化开销。
    /// </remarks>
    /// <param name="services">待填充的服务集合。</param>
    /// <returns>同一个 <paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddAppLogging(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddNLog(_ => NLogSetup.LogFactory);
        });

        return services;
    }
}
