using Avalonia.Controls;
using BiliMusicDotNet;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Models.Navigation;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// Headless 测试的最小服务容器。
/// </summary>
/// <remarks>
/// 注册面与各平台入口保持一致（视图模型 + 视图 + 主视图服务 + 登录服务），
/// 但把登录服务换成不落盘、不联网的替身：测试不该读到开发机上的真实凭据。
/// </remarks>
internal static class HeadlessServices
{
    /// <summary>构建容器。</summary>
    /// <returns>可直接赋给 <c>App.Services</c> 的服务提供者。</returns>
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // 视图模型都以 ILogger<T> 为构造依赖；测试不需要日志输出，注册空实现即可。
        services.AddLogging();

        services.AddSingleton<ViewLocator>();
        services.AddSingleton<IMainViewService, StubMainViewService>();

        // 登录服务的替身：不落盘、不联网、状态恒为未登录。
        services.AddSingleton<IBiliLoginService, StubLoginService>();
        services.AddSingleton<ICredentialService, CredentialService>();

        services.AddSingleton<MainViewModel>();
        services.AddTransient<AboutPageViewModel>();
        services.AddTransient<RecommendationPageViewModel>();
        services.AddTransient<BiliMusicDotNet.Views.AboutPageView>();
        services.AddTransient<BiliMusicDotNet.Views.RecommendationPageView>();

        return services.BuildServiceProvider();
    }

    /// <summary>把主视图造成一个空窗口，避免测试真的去开平台窗口。</summary>
    private sealed class StubMainViewService : IMainViewService
    {
        public Control CreateMainView(MainViewModel viewModel) => new Window { DataContext = viewModel };
    }

    /// <summary>不落盘、不联网的登录服务替身；所有登录动作都停在"未登录"。</summary>
    private sealed class StubLoginService : IBiliLoginService
    {
        // 三个事件是接口成员，替身只做订阅点：测试不通过它触发，故显式声明"未使用"。
#pragma warning disable CS0067
        public event EventHandler<LoginStateChangedEventArgs>? StateChanged;
        public event EventHandler<LoginSucceededEventArgs>? LoginSucceeded;
        public event EventHandler<LoggedOutEventArgs>? LoggedOut;
#pragma warning restore CS0067

        public LoginState State => LoginState.Idle;

        public BiliUserProfile? CurrentProfile => null;

        public bool IsLoggedIn => false;

        public Task<QrSession> CreateQrSessionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试替身不提供取码");

        public Task<QrStatusResult> PollQrStatusAsync(QrSession session, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试替身不提供轮询");

        public Task<QrLoginResult> WaitForConfirmationAsync(
            QrSession session,
            IProgress<QrStatusResult>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试替身不提供轮询");

        public Task<BiliUserProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<BiliUserProfile?>(null);

        public Task<byte[]?> GetAvatarImageAsync(Uri? imageUri, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);

        public Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public void Logout()
        {
        }

        public void Dispose()
        {
        }
    }
}
