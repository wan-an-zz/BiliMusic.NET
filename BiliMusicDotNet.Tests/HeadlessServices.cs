using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using BiliMusicDotNet;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Desktop.Services;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
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
    /// <param name="loginService">
    /// 登录服务替身；不传时内部新建一个。测试传入自己那份即可在用例里触发事件、改返回值。
    /// </param>
    /// <returns>可直接赋给 <c>App.Services</c> 的服务提供者。</returns>
    public static IServiceProvider Build(StubLoginService? loginService = null)
    {
        var services = new ServiceCollection();

        // 视图模型都以 ILogger<T> 为构造依赖；测试不需要日志输出，注册空实现即可。
        services.AddLogging();

        services.AddSingleton<ViewLocator>();
        services.AddSingleton<IMainViewService, StubMainViewService>();

        // 视图模型之间的消息通道；与生产注册保持一致，用进程级默认实现。
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

        // 登录服务的替身：不落盘、不联网、状态恒为未登录。
        services.AddSingleton<IBiliLoginService>(loginService ?? new StubLoginService());
        services.AddSingleton<ICredentialService, CredentialService>();

        services.AddSingleton<IEncryptService, DpapiEncryptService>();

        services.AddSingleton<MainViewModel>();
        services.AddTransient<AboutPageViewModel>();
        services.AddTransient<RecommendationPageViewModel>();
        services.AddTransient<BiliMusicDotNet.Views.AboutPageView>();
        services.AddTransient<BiliMusicDotNet.Views.RecommendationPageView>();
        services.AddTransient<BiliMusicDotNet.Views.QrLoginView>();

        return services.BuildServiceProvider();
    }

    /// <summary>把主视图造成一个空窗口，避免测试真的去开平台窗口。</summary>
    private sealed class StubMainViewService : IMainViewService
    {
        public Control CreateMainView(MainViewModel viewModel) => new Window { DataContext = viewModel };
    }

    /// <summary>
    /// 不落盘、不联网的登录服务替身。
    /// </summary>
    /// <remarks>
    /// 默认停在"未登录"；用例可以改 <see cref="IsLoggedIn"/>、预置取码结果与轮询终局，
    /// 或用 <see cref="RaiseLoginSucceeded"/> / <see cref="RaiseLoggedOut"/> 直接触发事件。
    /// </remarks>
    internal sealed class StubLoginService : IBiliLoginService
    {
        // StateChanged 是接口成员，替身用不上：登录流程的状态推进靠 WaitForConfirmationAsync 的返回值表达。
#pragma warning disable CS0067
        public event EventHandler<LoginStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067
        public event EventHandler<LoginSucceededEventArgs>? LoginSucceeded;
        public event EventHandler<LoggedOutEventArgs>? LoggedOut;

        public LoginState State { get; set; } = LoginState.Idle;

        public BiliUserProfile? CurrentProfile { get; set; }

        public bool IsLoggedIn { get; set; }

        /// <summary>取码结果；为 <see langword="null"/> 时抛 <see cref="HttpRequestException"/> 模拟取码失败。</summary>
        public QrSession? QrSession { get; set; }

        /// <summary>轮询终局。</summary>
        public QrLoginResult QrResult { get; set; } =
            new(QrLoginOutcome.Expired, null, "二维码已失效");

        /// <summary>头像字节；为 <see langword="null"/> 表示取不到。</summary>
        public byte[]? AvatarBytes { get; set; }

        /// <summary>登出时是否抛 <c>StorageException</c>，用于验证"删盘失败仍按已登出渲染"。</summary>
        public bool ThrowOnLogout { get; set; }

        /// <summary>被调用过的取码次数。</summary>
        public int CreateSessionCallCount { get; private set; }

        public void RaiseLoginSucceeded(BiliUserProfile? profile)
            => LoginSucceeded?.Invoke(this, new LoginSucceededEventArgs(profile));

        public void RaiseLoggedOut()
            => LoggedOut?.Invoke(this, new LoggedOutEventArgs());

        public Task<QrSession> CreateQrSessionAsync(CancellationToken cancellationToken = default)
        {
            CreateSessionCallCount++;

            if (QrSession is not { } session)
            {
                throw new HttpRequestException("模拟取码失败");
            }

            return Task.FromResult(session);
        }

        public Task<QrStatusResult> PollQrStatusAsync(QrSession session, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("测试替身不提供单次轮询");

        public Task<QrLoginResult> WaitForConfirmationAsync(
            QrSession session,
            IProgress<QrStatusResult>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(QrResult);

        public Task<BiliUserProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentProfile);

        public Task<byte[]?> GetAvatarImageAsync(Uri? imageUri, CancellationToken cancellationToken = default)
            => Task.FromResult(AvatarBytes);

        public Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(IsLoggedIn);

        public void Logout()
        {
            if (ThrowOnLogout)
            {
                throw new StorageException("删除", @"X:\不可写\credentials.dat", new IOException("模拟删除失败"));
            }

            IsLoggedIn = false;
            CurrentProfile = null;
            RaiseLoggedOut();
        }

        public void Dispose()
        {
        }
    }
}
