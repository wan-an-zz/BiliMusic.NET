using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Logging;
using BiliMusicDotNet.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BiliMusicDotNet;

public partial class App : Application
{
    /// <summary>
    /// 应用级服务容器（组合根）。由各平台入口在启动 Avalonia 之前注入：
    /// 桌面端在 <c>Program.Main</c>，Android / iOS / 浏览器在各自的 <c>CustomizeAppBuilder</c> / <c>Main</c>。
    /// </summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        // 先把日志管道建好（含 Avalonia 内部日志转发），再加载 XAML，启动过程的日志才不会丢。
        NLogSetup.Initialize();
        Avalonia.Logging.Logger.Sink = new NLogSink(NLogSetup.LogFactory);

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = Services ?? throw new InvalidOperationException(
            "App.Services 未初始化：请先在各平台入口（Program.Main / CustomizeAppBuilder）注册服务，再启动 Avalonia。");

        // 视图定位器同样由容器创建（其构造函数依赖 IServiceProvider），再挂到应用级数据模板。
        DataTemplates.Add(services.GetRequiredService<ViewLocator>());

        // App 不认识具体界面：主视图完全由各平台的 IMainViewService 决定。
        var viewModel = services.GetRequiredService<MainViewModel>();
        var viewService = services.GetRequiredService<IMainViewService>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
            {
                var mainView = viewService.CreateMainView(viewModel);
                desktop.MainWindow = mainView as Window ?? new Window { Content = mainView };
                break;
            }

            case IActivityApplicationLifetime activity:
            {
                // 单活动平台可能反复创建 Activity：每次都取新的视图实例（一个控件只能有一个父级）。
                activity.MainViewFactory = () => viewService.CreateMainView(viewModel);
                break;
            }

            case ISingleViewApplicationLifetime singleView:
            {
                singleView.MainView = viewService.CreateMainView(viewModel);
                break;
            }
        }

        services.GetRequiredService<ILogger<App>>()
            .LogInformation("应用启动完成（生命周期：{Lifetime}，主视图服务：{ViewService}）",
                ApplicationLifetime?.GetType().Name ?? "未知",
                viewService.GetType().Name);

        base.OnFrameworkInitializationCompleted();
    }
}
