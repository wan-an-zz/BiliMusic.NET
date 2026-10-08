using System;
using Avalonia.Logging;
using NLog;

namespace BiliMusicDotNet.Logging;

/// <summary>
/// 把 Avalonia 内部日志转发到 NLog，使框架日志与应用日志进入同一条管道、受同一份规则约束。
/// </summary>
public sealed class NLogSink : ILogSink
{
    private readonly LogFactory _logFactory;

    /// <summary>
    /// 创建转发目标。
    /// </summary>
    /// <param name="logFactory">与应用共用的 NLog 日志工厂。</param>
    public NLogSink(LogFactory logFactory) => _logFactory = logFactory;

    /// <inheritdoc />
    public bool IsEnabled(LogEventLevel level, string area)
        => GetLogger(area).IsEnabled(ToNLogLevel(level));

    /// <inheritdoc />
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (level == LogEventLevel.Fatal ||
            level == LogEventLevel.Error ||
            level == LogEventLevel.Warning)
        {
            GetLogger(area).Log(ToNLogLevel(level), messageTemplate);
        }
    }

    /// <inheritdoc />
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
        params object?[] propertyValues)
    {
        if (level == LogEventLevel.Fatal ||
            level == LogEventLevel.Error ||
            level == LogEventLevel.Warning)
        {
            GetLogger(area).Log(ToNLogLevel(level), messageTemplate, propertyValues);
        }
    }

    // 用 area 作为 logger 名（Avalonia.Renderer、Avalonia.Binding …），便于按来源单独配置规则。
    private NLog.Logger GetLogger(string area)
        => _logFactory.GetLogger(string.IsNullOrEmpty(area) ? "Avalonia" : "Avalonia." + area);

    private static LogLevel ToNLogLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Warning => LogLevel.Warn,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Fatal,
        _ => LogLevel.Warn,
    };
}
