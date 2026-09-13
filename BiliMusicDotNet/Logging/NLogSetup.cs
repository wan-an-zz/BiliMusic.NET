using System;
using System.IO;
using NLog;

namespace BiliMusicDotNet.Logging;

/// <summary>
/// NLog 启动引导：幂等加载 <c>nlog.config</c>，并暴露全局唯一的日志工厂。
/// </summary>
public static class NLogSetup
{
    /// <summary>配置文件相对应用根目录的文件名。</summary>
    public const string ConfigurationFileName = "nlog.config";

    private static readonly object SyncRoot = new();
    private static LogFactory? _logFactory;
    private static bool _initialized;

    /// <summary>
    /// 全局日志工厂。<see cref="Microsoft.Extensions.Logging"/> 提供程序与 Avalonia 内部日志转发共用同一实例，
    /// 保证两边都受同一份 nlog.config 规则约束。
    /// </summary>
    public static LogFactory LogFactory => _logFactory ??= LogManager.Setup().LogFactory;

    /// <summary>
    /// 加载配置文件完成日志初始化；重复调用不会重复加载（幂等）。
    /// </summary>
    /// <remarks>
    /// 配置文件缺失时**不阻断启动**：NLog 保持默认配置（此时日志会被丢弃），并在标准错误给出提示，
    /// 避免"日志静默消失"这类难排查的发布问题。配置内容写错时则会立即抛出（配置里已开启 throwConfigExceptions）。
    /// </remarks>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            var configFile = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);

            if (File.Exists(configFile))
            {
                _logFactory = LogManager.Setup().LoadConfigurationFromFile(configFile).LogFactory;
            }
            else
            {
                Console.Error.WriteLine(
                    $"[NLog] 未找到配置文件：{configFile}。日志将不会被输出，请确认 {ConfigurationFileName} 已随程序发布。");
            }

            _initialized = true;
        }
    }
}
