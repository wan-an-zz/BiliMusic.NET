using System;
using System.Threading;
using System.Threading.Tasks;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;

namespace BiliMusicDotNet.Abstracts;

/// <summary>
/// B 站扫码登录服务：取码 → 轮询扫码状态 → 拉取用户昵称/头像 → 触发事件通知 UI。
/// </summary>
public interface IBiliLoginService : IDisposable
{
    /// <summary>
    /// 登录状态发生变化（取码、扫码、确认、成功、失效、登出）时触发。
    /// </summary>
    /// <remarks>
    /// 事件参数中的 <see cref="LoginStateChangedEventArgs.State"/> 即"当前完整状态"，
    /// UI 只需按它渲染，无需自行维护状态机；判定为终态的取值见
    /// <see cref="IsTerminal"/>。
    /// </remarks>
    event EventHandler<LoginStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 登录成功（已拿到 Cookie 且已尝试抓取用户信息）时触发，便于 UI 执行"登录后"动作（刷新头像、进入主界面）。
    /// </summary>
    /// <remarks>
    /// 触发时凭据已经写入 <see cref="ICredentialService.Current"/>；<see cref="CurrentProfile"/> 可能为空
    /// （资料接口临时失败不影响登录成立），UI 需允许空资料并自行重试 <see cref="GetProfileAsync"/>。
    /// </remarks>
    event EventHandler<LoginSucceededEventArgs>? LoginSucceeded;

    /// <summary>
    /// 登出完成时触发；UI 应清空头像、昵称等展示状态。
    /// </summary>
    event EventHandler<LoggedOutEventArgs>? LoggedOut;

    /// <summary>最近一次已知的登录状态。</summary>
    LoginState State { get; }

    /// <summary>当前用户信息（昵称、头像地址等）；未抓到时为 <see langword="null"/>。</summary>
    BiliUserProfile? CurrentProfile { get; }

    /// <summary>
    /// 是否处于已登录状态。
    /// </summary>
    /// <remarks>
    /// 判定依据是 <see cref="ICredentialService.Current"/> 非空——凭据由凭据服务持有，
    /// 本接口不再暴露凭据本身。
    /// </remarks>
    bool IsLoggedIn { get; }

    /// <summary>
    /// 第一步：向 B 站申请一个登录二维码。
    /// </summary>
    /// <param name="cancellationToken">取消令牌，用于用户关闭登录界面时中断请求。</param>
    /// <returns>
    /// 一次二维码会话。返回值中的 <see cref="QrSession.Content"/> 是二维码<b>内容字符串</b>（形如
    /// <c>https://www.bilibili.com/h5/login?...</c>），<b>由 UI 层用二维码库渲染成图片</b>；
    /// <see cref="QrSession.Key"/> 是后续轮询凭据，业务层已自行保管，UI 无需传递。
    /// </returns>
    /// <exception cref="BiliApiException">接口返回非 0 业务码，或响应缺少二维码字段。</exception>
    /// <exception cref="HttpRequestException">网络层失败（离线、DNS、TLS 等）。</exception>
    /// <exception cref="TaskCanceledException">请求超时或被 <paramref name="cancellationToken"/> 取消。</exception>
    Task<QrSession> CreateQrSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 第二步：查询一次扫码状态（单次轮询，不含内部循环）。
    /// </summary>
    /// <param name="session">由 <see cref="CreateQrSessionAsync"/> 返回的会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 本次查询结果。状态取自响应<b>内层</b>的 <c>data.code</c>
    /// （外层 <c>code</c> 只表示接口调用是否成功，读错层会永远停在"未扫码"）。
    /// 接口返回的未知状态码会被原样保留在 <see cref="QrStatusResult.RawStatus"/> 中而不抛异常，仅记录日志。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="HttpRequestException">网络层失败。</exception>
    /// <exception cref="TaskCanceledException">请求超时或被取消。</exception>
    Task<QrStatusResult> PollQrStatusAsync(QrSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// 第三步（推荐入口）：轮询扫码状态直到出现终态，成功后自动抓取昵称与头像。
    /// </summary>
    /// <param name="session">由 <see cref="CreateQrSessionAsync"/> 返回的会话。</param>
    /// <param name="progress">可选的进度回调，每次状态变化时报告一次（UI 可据此提示"已扫码，请在手机上确认"）。</param>
    /// <param name="cancellationToken">取消令牌；用户关闭登录界面时必须取消。</param>
    /// <returns>
    /// 轮询终局。仅当 <see cref="QrLoginResult.Outcome"/> 为 <see cref="QrLoginOutcome.Succeeded"/> 时
    /// <see cref="QrLoginResult.Credentials"/> 才非空（凭据同时已写入 <see cref="ICredentialService.Current"/>）。
    /// 返回 <see cref="QrLoginOutcome.TimedOut"/> 或 <see cref="QrLoginOutcome.NetworkFailed"/> 时，
    /// 会话可能仍然有效，UI 可提示用户后重新调用本方法续等（不必重新取码）。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> 已被取消（<b>不</b>吞掉取消：取消是正常出口，由调用方决定如何处理）。
    /// </exception>
    Task<QrLoginResult> WaitForConfirmationAsync(
        QrSession session,
        IProgress<QrStatusResult>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 抓取当前登录用户的昵称、头像地址等信息（内部调用 <c>nav</c> 接口）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 用户信息；未登录（接口返回 <c>-101</c>）或凭据已失效时返回 <see langword="null"/>，
    /// 并同步把 <see cref="State"/> 置为 <see cref="LoginState.Expired"/>、清空凭据。
    /// </returns>
    /// <exception cref="HttpRequestException">网络层失败。</exception>
    /// <exception cref="TaskCanceledException">请求超时或被取消。</exception>
    Task<BiliUserProfile?> GetProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载头像图片原始字节，交给 UI 解码显示（服务层不引用任何 UI/图像类型）。
    /// </summary>
    /// <param name="imageUri">头像地址，通常来自 <see cref="BiliUserProfile.AvatarUri"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>图片字节；地址为空、请求失败或返回空内容时返回 <see langword="null"/>（不抛出，避免拖垮登录流程）。</returns>
    Task<byte[]?> GetAvatarImageAsync(Uri? imageUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验并恢复一次既有登录态（典型场景：<see cref="ICredentialService.LoadAsync"/> 读回凭据后调用）。
    /// </summary>
    /// <param name="credentials">
    /// 待恢复的凭据。无论来源是磁盘读回还是调用方自行组装，都会先交给
    /// <see cref="ICredentialService"/> 持有（并落盘）。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 校验通过并成功取回用户信息时返回 <see langword="true"/>；
    /// 否则返回 <see langword="false"/> 并清空登录态（含磁盘副本）。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="credentials"/> 为 <see langword="null"/>。</exception>
    Task<bool> TryRestoreSessionAsync(BiliLoginCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登出：清除本地 Cookie、内存中的登录态与磁盘上的凭据副本，并触发 <see cref="LoggedOut"/>。
    /// </summary>
    /// <remarks>
    /// 本方法<b>只做本地清理</b>：不调用 B 站登出接口，不影响其它设备的登录状态。
    /// </remarks>
    /// <exception cref="Models.Exceptions.StorageException">
    /// 磁盘副本删除失败。此时 Cookie 与内存态已清理完毕，但本地仍留着等价于登录态的凭据。
    /// </exception>
    void Logout();

    /// <summary>
    /// 判定某个登录状态是否为终态（到达后无需再轮询）。
    /// </summary>
    /// <param name="state">待判定的状态。</param>
    /// <returns>是终态时为 <see langword="true"/>。</returns>
    public static bool IsTerminal(LoginState state)
        => state is LoginState.LoggedIn or LoginState.Expired or LoginState.TimedOut or LoginState.Failed;
}
