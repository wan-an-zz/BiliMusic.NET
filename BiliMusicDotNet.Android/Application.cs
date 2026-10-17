using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Android.Services;
using BiliMusicDotNet.DependencyInjection;
using BiliMusicDotNet.Fonts;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            // Activity / 进程重建时本方法会再次调用：容器只构建一次。
            App.Services ??= BuildServices();

            return base.CustomizeAppBuilder(builder)
                .WithHarmonyOsSansFont()
                .WithInterFont();
        }

        private static IServiceProvider BuildServices()
        {
            var services = new ServiceCollection()
                .AddBiliMusicDotNet()
                // 平台特定实现
                .AddSingleton<IMainViewService, AndroidMainViewService>()
                .AddSingleton<IEncryptService, AndroidKeystoreEncryptService>();

            // 构建期校验：注册缺失 / 生命周期错误在启动时立即暴露。
            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }
    }
}
