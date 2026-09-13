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
/// UI 层应在处理器里自行做一次线程切换，不要把线程模型当作契约的一部分。
/// </para>
/// <para>
/// <b>安全约定</b>：凭据中的 <c>SESSDATA</c> 等价于账号登录态。
/// 本服务只把它保存在内存中，不做任何落盘；若需"下次启动免登录"，
/// 由 UI/宿主层使用平台凭据保护机制加密持久化，严禁明文写入配置或日志。
/// </para>
/// </remarks>
public interface IBiliLoginService : IDisposable
{
    /// <summary>
    /// 登录状态发生变化（取码、扫码、确认、成功、失效、登出）时触发。
    /// </summary>
    /// <remarks>
    /// 事件参数中的 <see cref="LoginStateChangedEventArgs.State"/> 即"当前完整状态"，
    /// UI 只需按它渲染，无需自行维护状态机。
    /// </remarks>
    event EventHandler<LoginStateChangedEventArgs>? StateChanged;

    /// <summary>最近一次已知的登录状态。</summary>
    LoginState State { get; }

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
    /// 一次扫码登录会话。
    /// </summary>
    /// <param name="Key">轮询凭据（<c>qrcode_key</c>），业务层已保管，UI 一般不需要使用。</param>
    /// <param name="Content">二维码内容字符串，需由 UI 渲染成二维码图片。</param>
    /// <param name="ExpiresAt">本地推算的失效时刻（B 站二维码有效期约 3 分钟），UI 可用于画倒计时/进度。</param>
    public sealed record QrSession(string Key, string Content, DateTimeOffset ExpiresAt);

    /// <summary>
    /// 登录状态机（UI 渲染依据）。
    /// </summary>
    public enum LoginState
    {
        /// <summary>未登录且无进行中的扫码流程。</summary>
        Idle,

        /// <summary>已取到二维码，等待用户扫码。</summary>
        WaitingForScan,
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
