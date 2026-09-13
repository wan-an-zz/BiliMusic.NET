using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiliMusicDotNet.Abstracts;

/// <summary>
/// B 站扫码登录服务：取码 → 轮询扫码状态 → 拉取用户昵称/头像 → 触发事件通知 UI。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>：本服务只负责登录<b>业务流程</b>，不产生任何 UI 类型（不渲染二维码图片、不加载头像控件）。
/// 二维码渲染、头像显示、界面状态绑定均由 UI 层依据本接口的返回值与事件自行实现，
/// 契约见 <c>docs/Services/Login/LoginService文档.md</c>。
/// </para>
/// <para>
/// <b>线程约定</b>：所有异步方法都可从任意线程调用；事件在触发它的那个线程上<b>同步</b>回调。
/// 由于流程由 UI 发起的 <c>await</c> 驱动，事件通常在 UI 线程上到达——但 UI 层仍应在处理器里
/// 自行做一次线程切换（例如 Avalonia 的 <c>Dispatcher.UIThread.Invoke</c>），不要把线程模型当作契约的一部分。
/// </para>
/// <para>
/// <b>终止约定</b>：<see cref="WaitForConfirmationAsync"/> 有且只有四种出口——
/// 登录成功、二维码失效、调用方取消（<see cref="CancellationToken"/>）、二维码有效期兜底超时。
/// 调用方关闭登录界面时必须取消令牌，否则轮询会继续空转到有效期结束。
/// </para>
/// <para>
/// <b>安全约定</b>：<see cref="BiliLoginCredentials"/> 中的 <c>SESSDATA</c> 等价于账号登录态。
/// 本服务只把它保存在内存中（<see cref="CurrentCredentials"/>），不做任何落盘；
/// 若需"下次启动免登录"，由 UI/宿主层使用平台凭据保护机制（Windows DPAPI、macOS Keychain、
/// Linux Secret Service）加密持久化，严禁明文写入配置或日志。
/// </para>
/// </remarks>
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
    /// 触发时 <see cref="CurrentCredentials"/> 必定非空；<see cref="CurrentProfile"/> 可能为空
    /// （资料接口临时失败不影响登录成立），UI 需允许空资料并自行重试 <see cref="GetProfileAsync"/>。
    /// </remarks>
    event EventHandler<LoginSucceededEventArgs>? LoginSucceeded;

    /// <summary>
    /// 登出完成时触发；UI 应清空头像、昵称等展示状态。
    /// </summary>
    event EventHandler<LoggedOutEventArgs>? LoggedOut;

    /// <summary>最近一次已知的登录状态。</summary>
    LoginState State { get; }

    /// <summary>当前登录凭据；未登录时为 <see langword="null"/>。</summary>
    BiliLoginCredentials? CurrentCredentials { get; }

    /// <summary>当前用户信息（昵称、头像地址等）；未抓到时为 <see langword="null"/>。</summary>
    BiliUserProfile? CurrentProfile { get; }

    /// <summary>是否处于已登录状态（<see cref="CurrentCredentials"/> 非空）。</summary>
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
    /// <see cref="QrLoginResult.Credentials"/> 才非空。
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
    /// 校验并恢复一次既有登录态（典型场景：宿主层从加密存储里读回 Cookie 后调用）。
    /// </summary>
    /// <param name="credentials">待恢复的凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验通过并成功取回用户信息时返回 <see langword="true"/>；否则返回 <see langword="false"/> 并清空登录态。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="credentials"/> 为 <see langword="null"/>。</exception>
    Task<bool> TryRestoreSessionAsync(BiliLoginCredentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登出：清除本地 Cookie 与内存中的登录态，并触发 <see cref="LoggedOut"/>。
    /// </summary>
    /// <remarks>
    /// 本方法<b>只做本地清理</b>：不调用 B 站登出接口，不影响其它设备的登录状态。
    /// 若持久化过凭据，调用方必须同时删除持久化副本。
    /// </remarks>
    void Logout();

    /// <summary>
    /// 一次扫码登录会话。
    /// </summary>
    /// <param name="Key">轮询凭据（<c>qrcode_key</c>），业务层已保管，UI 一般不需要使用。</param>
    /// <param name="Content">二维码内容字符串，需由 UI 渲染成二维码图片。</param>
    /// <param name="ExpiresAt">本地推算的失效时刻（B 站二维码有效期约 3 分钟），UI 可用于画倒计时/进度。</param>
    public sealed record QrSession(string Key, string Content, DateTimeOffset ExpiresAt);

    /// <summary>
    /// 一次扫码状态查询的结果。
    /// </summary>
    /// <param name="State">归一化后的状态。</param>
    /// <param name="RawStatus">B 站原始状态码（<c>data.code</c>），未知状态时保留原值便于排查。</param>
    /// <param name="Message">接口返回的提示文本（已剥除 <c>&amp;nbsp;</c> 等 HTML 残留），可直接展示。</param>
    /// <param name="Credentials">仅当本次查询即登录成功时非空。</param>
    public sealed record QrStatusResult(
        LoginState State,
        int RawStatus,
        string Message,
        BiliLoginCredentials? Credentials);

    /// <summary>
    /// 轮询的终局结果。
    /// </summary>
    /// <param name="Outcome">终局类型。</param>
    /// <param name="Credentials">仅登录成功时非空。</param>
    /// <param name="Profile">仅登录成功时非空，且资料接口可能失败（此时为 <see langword="null"/>）。</param>
    /// <param name="Message">面向用户的说明文本。</param>
    public sealed record QrLoginResult(
        QrLoginOutcome Outcome,
        BiliLoginCredentials? Credentials,
        BiliUserProfile? Profile,
        string Message);

    /// <summary>
    /// 轮询可能的终局类型。
    /// </summary>
    public enum QrLoginOutcome
    {
        /// <summary>登录成功，已拿到 Cookie。</summary>
        Succeeded,

        /// <summary>二维码已失效，必须重新调用 <see cref="CreateQrSessionAsync"/> 取新码。</summary>
        Expired,

        /// <summary>超过二维码有效期（约 3 分钟）仍未完成扫码，本地兜底退出。</summary>
        TimedOut,

        /// <summary>连续网络失败达到上限，会话可能仍有效，可提示用户后重试。</summary>
        NetworkFailed,
    }

    /// <summary>
    /// 登录状态机（UI 渲染依据）。
    /// </summary>
    public enum LoginState
    {
        /// <summary>未登录且无进行中的扫码流程。</summary>
        Idle,

        /// <summary>已取到二维码，等待用户扫码。</summary>
        WaitingForScan,

        /// <summary>用户已扫码，等待其在手机上点击确认。</summary>
        WaitingForConfirm,

        /// <summary>登录成功，凭据已就绪。</summary>
        LoggedIn,

        /// <summary>二维码失效，需重新取码。</summary>
        Expired,

        /// <summary>等待超时，需重新取码。</summary>
        TimedOut,

        /// <summary>网络连续失败导致流程中断。</summary>
        Failed,
    }

    /// <summary>
    /// 判定某个登录状态是否为终态（到达后无需再轮询）。
    /// </summary>
    /// <param name="state">待判定的状态。</param>
    /// <returns>是终态时为 <see langword="true"/>。</returns>
    public static bool IsTerminal(LoginState state)
        => state is LoginState.LoggedIn or LoginState.Expired or LoginState.TimedOut or LoginState.Failed;

    /// <summary>
    /// 登录成功后可用的凭据（与写操作所需的 CSRF 令牌）。
    /// </summary>
    /// <param name="SessData">登录凭据，等价于账号登录态，<b>必须加密存储</b>。</param>
    /// <param name="BiliJct">CSRF 令牌，所有写操作（点赞/收藏等）需作为 <c>csrf</c> 参数提交。</param>
    /// <param name="DedeUserId">当前用户 <c>mid</c>。</param>
    /// <param name="CookieHeader">
    /// 可直接用于请求的 Cookie 头（B 站返回的全部 Set-Cookie 原样拼接）。
    /// 仅在与 B 站域名通信时附加，禁止拼进 URL、禁止整体写日志。
    /// </param>
    public sealed record BiliLoginCredentials(
        string SessData,
        string BiliJct,
        string DedeUserId,
        string CookieHeader)
    {
        /// <summary>三个关键 Cookie 是否齐备（齐备即可判定为已登录）。</summary>
        public bool IsComplete =>
            !string.IsNullOrEmpty(SessData) &&
            !string.IsNullOrEmpty(BiliJct) &&
            !string.IsNullOrEmpty(DedeUserId);
    }

    /// <summary>
    /// 当前登录用户的展示信息。
    /// </summary>
    /// <param name="Mid">用户 ID。</param>
    /// <param name="Nickname">昵称。</param>
    /// <param name="AvatarUri">头像地址（已把 <c>//</c> 与 <c>http://</c> 统一升为 <c>https://</c>）。</param>
    /// <param name="IsVip">是否大会员（<c>vipStatus == 1</c>）。</param>
    /// <param name="Level">用户等级，资料缺失时为 0。</param>
    public sealed record BiliUserProfile(
        long Mid,
        string Nickname,
        Uri? AvatarUri,
        bool IsVip,
        int Level);

    /// <summary>
    /// 登录状态变化事件参数。
    /// </summary>
    /// <param name="State">变化后的状态。</param>
    /// <param name="Message">面向用户的说明文本。</param>
    /// <param name="Profile">已登录时携带用户信息，否则为 <see langword="null"/>。</param>
    public sealed class LoginStateChangedEventArgs(LoginState State, string Message, BiliUserProfile? Profile) : EventArgs
    {
        /// <summary>变化后的状态。</summary>
        public LoginState State { get; } = State;

        /// <summary>面向用户的说明文本，可直接展示在登录界面上。</summary>
        public string Message { get; } = Message;

        /// <summary>已登录时携带用户信息。</summary>
        public BiliUserProfile? Profile { get; } = Profile;
    }

    /// <summary>
    /// 登录成功事件参数。
    /// </summary>
    /// <param name="Credentials">本次登录取得的凭据。</param>
    /// <param name="Profile">用户信息；资料接口失败时为 <see langword="null"/>。</param>
    public sealed class LoginSucceededEventArgs(BiliLoginCredentials Credentials, BiliUserProfile? Profile) : EventArgs
    {
        /// <summary>本次登录取得的凭据。</summary>
        public BiliLoginCredentials Credentials { get; } = Credentials;

        /// <summary>用户信息（昵称、头像）。</summary>
        public BiliUserProfile? Profile { get; } = Profile;
    }

    /// <summary>
    /// 登出事件参数。
    /// </summary>
    public sealed class LoggedOutEventArgs : EventArgs;

    /// <summary>
    /// B 站接口返回非 0 业务码时抛出。
    /// </summary>
    /// <param name="code">B 站业务码（如 <c>-101</c> 未登录、<c>-412</c> 被风控拦截）。</param>
    /// <param name="message">接口返回的提示文本。</param>
    /// <param name="requestPath">出错的接口路径，便于定位。</param>
    public sealed class BiliApiException(int code, string message, string requestPath)
        : Exception($"Bilibili API [{code}] {message}（{requestPath}）")
    {
        /// <summary>B 站业务码。</summary>
        public int Code { get; } = code;

        /// <summary>出错的接口路径。</summary>
        public string RequestPath { get; } = requestPath;
    }
}
