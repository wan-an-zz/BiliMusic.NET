using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BiliMusicDotNet.DependencyInjection;
using BiliMusicDotNet.Logging;
using BiliMusicDotNet.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BiliMusicDotNet;

public partial class App : Application
{
    private IServiceProvider? _services;

    /// <summary>
    /// 应用级服务容器（组合根）。首次访问时构建，之后全局复用。
    /// </summary>
    public IServiceProvider Services => _services ??= BuildServiceProvider();

    public override void Initialize()
    {
        // 先把日志管道建好（含 Avalonia 内部日志转发），再加载 XAML，启动过程的日志才不会丢。
        NLogSetup.Initialize();
        Avalonia.Logging.Logger.Sink = new NLogSink(NLogSetup.LogFactory);

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 视图定位器同样由容器创建（其构造函数依赖 IServiceProvider），再挂到应用级数据模板。
        DataTemplates.Add(Services.GetRequiredService<ViewLocator>());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory =
                () => Services.GetRequiredService<MainView>();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = Services.GetRequiredService<MainView>();
        }

        Services.GetRequiredService<ILogger<App>>()
            .LogInformation("应用启动完成，主视图已创建（生命周期：{Lifetime}）",
                ApplicationLifetime?.GetType().Name ?? "未知");

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 注册应用服务。平台 head 可重写此方法以追加平台专属注册。
    /// </summary>
    /// <param name="services">待填充的服务集合。</param>
    protected virtual void ConfigureServices(IServiceCollection services)
        => services.AddBiliMusicDotNet();

    /// <summary>
    /// 构建容器并开启构建期校验，让注册缺失、生命周期错误在启动时立即暴露，而不是等到某次解析才失败。
    /// </summary>
    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
