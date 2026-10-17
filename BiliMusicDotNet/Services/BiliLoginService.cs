using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Services;

/// <summary>
/// <see cref="IBiliLoginService"/> 的默认实现：基于 B 站 Web 扫码登录接口
/// （<c>passport.bilibili.com/x/passport-login/web/qrcode/*</c>）完成取码、轮询、拉取用户信息与事件通知。
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP 细节</b>：内部使用一个独立的 <see cref="HttpClient"/>，其 <see cref="CookieContainer"/>
/// 常驻，扫码成功后 B 站下发的 Cookie 会自动随后续请求发往 <c>*.bilibili.com</c>。
/// 轮询请求禁用自动重定向，以便直接观察登录成功时的 302 与 <c>Set-Cookie</c>。
/// </para>
/// <para>
/// <b>凭据归属</b>：本服务不持有凭据，运行期唯一的读取入口是 <see cref="ICredentialService.Current"/>；
/// 登录成功时落盘、登出与登录态失效时清理，都由它完成。
/// </para>
/// </remarks>
public sealed class BiliLoginService : IBiliLoginService
{
    /// <summary>登录相关接口的域名。</summary>
    private const string PassportOrigin = "https://passport.bilibili.com";

    /// <summary>登录接口要求的 Referer（与内容接口的取值不同，写错会被风控）。</summary>
    private const string LoginReferer = PassportOrigin + "/login";

    /// <summary>数据接口的域名（登录态查询 <c>nav</c> 在此域；凭据的 Cookie 域也以它为准）。</summary>
    private const string ApiOrigin = "https://api.bilibili.com";

    /// <summary>内容接口使用的 Referer。</summary>
    private const string ContentReferer = "https://www.bilibili.com";

    /// <summary>现代桌面浏览器 UA：默认 UA 中包含的 "unknown" 特征容易被风控针对。</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    /// <summary>二维码有效期（B 站实测约 3 分钟），既是会话的展示用失效时刻，也是轮询的兜底超时。</summary>
    private static readonly TimeSpan QrCodeLifetime = TimeSpan.FromMinutes(3);

    /// <summary>轮询间隔。低于 1 秒的请求特征会明显偏离正常客户端，不建议再压。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1800);

    /// <summary>单次轮询请求的超时；超时按"一次失败"处理，不中断整个流程。</summary>
    private static readonly TimeSpan PollRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>取码请求的超时（可交互路径，失败要快速反馈给用户）。</summary>
    private static readonly TimeSpan GenerateRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>连续网络失败多少次后放弃轮询。</summary>
    private const int MaxConsecutiveFailures = 5;

    /// <summary>B 站扫码状态码：未扫码。</summary>
    private const int RawNotScanned = 86101;

    /// <summary>B 站扫码状态码：已扫码待确认。</summary>
    private const int RawNotConfirmed = 86090;

    /// <summary>B 站扫码状态码：登录成功。</summary>
    private const int RawSuccess = 0;

    /// <summary>B 站扫码状态码：二维码已失效。</summary>
    private const int RawExpired = 86038;

    /// <summary>提示文本里的 HTML 实体/标签残留（接口会返回 <c>&amp;nbsp;</c>）。</summary>
    private static readonly Regex HtmlResidue = new(@"&nbsp;|<[^>]+>", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly ILogger<BiliLoginService> _logger;
    private readonly ICredentialService _credentialService;
    private readonly CookieContainer _cookies = new();

    /// <summary>保证同一时刻只有一个轮询在跑（同一个 qrcode_key 并发轮询属于异常调用）。</summary>
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    /// <summary>已处理过登录成功的 qrcode_key，避免重复轮询时重复触发登录事件。</summary>
    private readonly ConcurrentQueue<string> _processedKeys = new();

    private HttpClient _http;
    private bool _disposed;

    private BiliUserProfile? _profile;
    private LoginState _state = LoginState.Idle;

    /// <summary>
    /// 由容器创建：日志器与凭据服务由依赖注入提供，HTTP 传输用默认实现。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="credentialService">凭据的持有者与持久化入口。</param>
    public BiliLoginService(ILogger<BiliLoginService> logger, ICredentialService credentialService)
        : this(new HttpClientHandler(), logger, credentialService)
    {
    }

    /// <summary>
    /// 完整构造：显式指定 HTTP 传输、日志器与凭据服务。
    /// </summary>
    /// <param name="handler">
    /// HTTP 传输。会被包进 <see cref="HttpClient"/> 后由本服务持有并在 <see cref="Dispose"/> 时释放。
    /// </param>
    /// <param name="logger">日志器。</param>
    /// <param name="credentialService">凭据的持有者与持久化入口；登录结果会写给它。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public BiliLoginService(
        HttpMessageHandler handler,
        ILogger<BiliLoginService> logger,
        ICredentialService credentialService)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _credentialService = credentialService ?? throw new ArgumentNullException(nameof(credentialService));

        if (handler is HttpClientHandler httpHandler)
        {
            httpHandler.UseCookies = true;
            httpHandler.CookieContainer = _cookies;
            // 轮询成功态可能是 302：必须能观察到它才能拿到 Set-Cookie。
            httpHandler.AllowAutoRedirect = false;
        }

        _http = CreateClient(handler);
    }

    /// <inheritdoc />
    public event EventHandler<LoginStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<LoginSucceededEventArgs>? LoginSucceeded;

    /// <inheritdoc />
    public event EventHandler<LoggedOutEventArgs>? LoggedOut;

    /// <inheritdoc />
    public LoginState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public bool IsLoggedIn => _credentialService.Current is not null;

    /// <inheritdoc />
    public BiliUserProfile? CurrentProfile
    {
        get
        {
            lock (_gate)
            {
                return _profile;
            }
        }
    }

    /// <inheritdoc />
    public async Task<QrSession> CreateQrSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var response = await SendAsync(
            HttpMethod.Get,
            new Uri(PassportOrigin + "/x/passport-login/web/qrcode/generate"),
            LoginReferer,
            cookieHeader: null,
            GenerateRequestTimeout,
            cancellationToken).ConfigureAwait(false);

        using var document = await ReadJsonAsync(response, "/x/passport-login/web/qrcode/generate", cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        var result = ReadApiResult(root, "/x/passport-login/web/qrcode/generate");

        if (result.Code != 0)
        {
            throw new BiliApiException(result.Code, result.Message, "/x/passport-login/web/qrcode/generate");
        }

        if (result.Data is not { } data ||
            !data.TryGetProperty("url", out var urlElement) ||
            !data.TryGetProperty("qrcode_key", out var keyElement) ||
            urlElement.GetString() is not { Length: > 0 } url ||
            keyElement.GetString() is not { Length: > 0 } key)
        {
            throw new BiliApiException(result.Code, "响应缺少 url / qrcode_key 字段", "/x/passport-login/web/qrcode/generate");
        }

        var session = new QrSession(key, url, DateTimeOffset.Now + QrCodeLifetime);
        _logger.LogInformation("已获取登录二维码，有效期至 {ExpiresAt:HH:mm:ss}", session.ExpiresAt);

        // 展示用状态；轮询入口会按实际扫码进度逐步推进。
        SetState(LoginState.WaitingForScan, "请使用哔哩哔哩手机客户端扫描二维码", profile: null);

        return session;
    }

    /// <inheritdoc />
    public Task<QrStatusResult> PollQrStatusAsync(QrSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return PollQrStatusAsync(session, publishState: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QrLoginResult> WaitForConfirmationAsync(
        QrSession session,
        IProgress<QrStatusResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ThrowIfDisposed();

        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is LoginState.Idle or LoginState.Expired or LoginState.TimedOut or LoginState.Failed)
            {
                SetState(LoginState.WaitingForScan, "请使用哔哩哔哩手机客户端扫描二维码", profile: null);
            }

            // 兜底超时取"二维码自身失效时刻"与"本次调用起点 + 有效期"中较早者：
            // 前者是 B 站定的时效，后者保证续等（TimedOut 后重入）不会无限循环。
            var now = DateTimeOffset.Now;
            var deadline = session.ExpiresAt < now + QrCodeLifetime ? session.ExpiresAt : now + QrCodeLifetime;

            var consecutiveFailures = 0;
            QrLoginResult? terminal = null;

            while (terminal is null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (DateTimeOffset.Now >= deadline)
                {
                    _logger.LogWarning("扫码登录超时（有效期 {Lifetime}）", QrCodeLifetime);
                    terminal = Fail(LoginState.TimedOut, "二维码已超时，请重新获取二维码", QrLoginOutcome.TimedOut);
                    break;
                }

                QrStatusResult status;
                try
                {
                    status = await PollQrStatusAsync(session, publishState: false, cancellationToken).ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 单次请求超时：按一次失败处理，继续轮询（不中断整条流程）。
                    _logger.LogWarning("扫码轮询单次请求超时（第 {Count} 次连续失败）", ++consecutiveFailures);
                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        terminal = Fail(LoginState.Failed, "网络异常，登录已中断，请稍后重试", QrLoginOutcome.NetworkFailed);
                        break;
                    }

                    await DelayBeforeNextPollAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "扫码轮询请求失败（第 {Count} 次连续失败）", ++consecutiveFailures);
                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        terminal = Fail(LoginState.Failed, "网络异常，登录已中断，请稍后重试", QrLoginOutcome.NetworkFailed);
                        break;
                    }

                    await DelayBeforeNextPollAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                progress?.Report(status);
                _logger.LogDebug("扫码状态：{State}（原始码 {Raw}）", status.State, status.RawStatus);

                switch (status.State)
                {
                    case LoginState.WaitingForScan:
                        SetState(LoginState.WaitingForScan, status.Message, profile: null);
                        break;

                    case LoginState.WaitingForConfirm:
                        SetState(LoginState.WaitingForConfirm, status.Message, profile: null);
                        break;

                    case LoginState.Expired:
                        terminal = Fail(LoginState.Expired, status.Message, QrLoginOutcome.Expired);
                        break;

                    case LoginState.LoggedIn:
                        terminal = await CompleteLoginAsync(session, cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        // 未知状态码：不中断流程，等它自己走到已知状态或超时。
                        _logger.LogWarning("扫码轮询遇到未预期状态码 {Raw}，继续轮询", status.RawStatus);
                        SetState(LoginState.WaitingForScan, status.Message, profile: null);
                        break;
                }

                if (terminal is null)
                {
                    await DelayBeforeNextPollAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return terminal;
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<BiliUserProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var cookieHeader = _cookies.GetCookieHeader(new Uri(ApiOrigin));
        if (string.IsNullOrEmpty(cookieHeader))
        {
            _logger.LogDebug("尚无登录 Cookie，跳过用户信息查询");
            return null;
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            new Uri(ApiOrigin + "/x/web-interface/nav"),
            ContentReferer,
            cookieHeader,
            PollRequestTimeout,
            cancellationToken).ConfigureAwait(false);

        using var document = await ReadJsonAsync(response, "/x/web-interface/nav", cancellationToken).ConfigureAwait(false);

        // nav 是特例：未登录时返回 -101，但 data 仍可能带 wbi_img。此处只关心登录信息，
        // 因此不能用"code != 0 即抛错"的统一封装。
        var result = ReadApiResult(document.RootElement, "/x/web-interface/nav");
        if (result.Code != 0)
        {
            _logger.LogWarning("用户信息查询失败：code={Code}，判定登录态失效", result.Code);
            ClearSession();
            ClearPersistedCredentialsSafely();
            SetState(LoginState.Expired, "登录已失效，请重新扫码登录", profile: null);
            return null;
        }

        if (result.Data is not { } data || data.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("用户信息响应缺少 data 对象");
            return null;
        }

        var profile = ReadProfile(data);
        if (profile is null)
        {
            _logger.LogWarning("用户信息响应缺少 mid / uname 字段");
            return null;
        }

        lock (_gate)
        {
            _profile = profile;
        }

        return profile;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAvatarImageAsync(Uri? imageUri, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (imageUri is null)
        {
            return null;
        }

        try
        {
            // 头像落在 hdslb.com CDN 上，缺少 Referer 会 403。
            using var response = await SendAsync(
                HttpMethod.Get,
                imageUri,
                ContentReferer,
                cookieHeader: null,
                PollRequestTimeout,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("头像下载失败：HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                _logger.LogWarning("头像下载返回空内容：{Uri}", imageUri);
                return null;
            }

            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // 头像只是展示要素：失败绝不能拖垮登录流程，交给 UI 用占位图兜底。
            _logger.LogWarning(ex, "头像下载异常：{Uri}", imageUri);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // 日志由凭据服务记录，这里只决定"要不要继续恢复"。
        if (!await _credentialService.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // LoadAsync 成功即保证 Current 非空；判定失效时可能已被清空，因此按可空处理。
        if (_credentialService.Current is not { IsComplete: true } credentials)
        {
            _logger.LogWarning("恢复登录态失败：凭据不完整");
            return false;
        }

        // 先让 Cookie 容器持有凭据，nav 才有机会返回 code 0。
        foreach (var (name, value) in ParseCookiePairs(credentials.CookieHeader))
        {
            SetCookie(name, value);
        }

        SetCookie("SESSDATA", credentials.SessData);
        SetCookie("bili_jct", credentials.BiliJct);
        SetCookie("DedeUserID", credentials.DedeUserId);

        var profile = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            // GetProfileAsync 判定失效时已经清过凭据，这里只负责告知调用方恢复失败。
            _logger.LogInformation("恢复登录态失败：凭据已失效");
            return false;
        }

        _logger.LogInformation("已恢复登录态：mid={Mid}", profile.Mid);
        SetState(LoginState.LoggedIn, "已自动登录", profile);
        RaiseLoginSucceeded(profile);
        return true;
    }

    /// <inheritdoc />
    public void Logout()
    {
        ThrowIfDisposed();

        // CookieContainer 没有公开的 Clear()，把已知域上的 Cookie 逐个置为过期即可等效清除。
        foreach (var domain in new[] { "bilibili.com", ".bilibili.com", "passport.bilibili.com", "api.bilibili.com" })
        {
            foreach (Cookie cookie in _cookies.GetCookies(new Uri($"https://{domain.TrimStart('.')}")))
            {
                cookie.Expired = true;
            }
        }

        ClearSession();

        // 磁盘副本删不掉时向上暴露：Cookie 与内存态已清干净，但本地仍留着等价于登录态的凭据。
        _credentialService.ClearAsync().GetAwaiter().GetResult();

        _logger.LogInformation("已登出并清除本地登录态");
        SetState(LoginState.Idle, "已退出登录", profile: null);

        var handler = LoggedOut;
        if (handler is not null)
        {
            handler(this, new LoggedOutEventArgs());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _pollGate.Dispose();
    }

    /// <summary>
    /// 单次状态查询。命中登录成功时抓取凭据并触发登录事件。
    /// </summary>
    /// <param name="session">扫码会话。</param>
    /// <param name="publishState">
    /// 是否把本次查询结果写入 <see cref="State"/> 并触发 <see cref="StateChanged"/>。
    /// 循环轮询时传 <see langword="false"/>，由循环统一推进状态，避免"状态抖动"。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task<QrStatusResult> PollQrStatusAsync(
        QrSession session,
        bool publishState,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var path = $"/x/passport-login/web/qrcode/poll?qrcode_key={Uri.EscapeDataString(session.Key)}";

        using var response = await SendAsync(
            HttpMethod.Get,
            new Uri(PassportOrigin + path),
            LoginReferer,
            cookieHeader: null,
            PollRequestTimeout,
            cancellationToken).ConfigureAwait(false);

        // 登录成功时 B 站可能直接回 302（而不是 200 + code 0）：此时响应体是空的，
        // 不能再去解析 JSON，只能从 Set-Cookie 判定成功。
        var setCookies = ReadSetCookies(response);

        QrStatusResult result;
        if (IsRedirect(response.StatusCode))
        {
            _logger.LogDebug("轮询命中重定向（HTTP {Status}），按登录成功处理", (int)response.StatusCode);
            PublishCredentials(setCookies);

            result = new QrStatusResult(LoginState.LoggedIn, RawSuccess, ResolveMessage(RawSuccess, null));
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var document = ParseJson(body, path);
            var outer = ReadApiResult(document.RootElement, path);

            // 内层 data.code 才是扫码状态；外层 code 只表示接口调用是否成功。
            var hasPayload = outer.Data is { } data && data.ValueKind == JsonValueKind.Object;
            var payload = hasPayload ? outer.Data!.Value : default;

            var rawStatus = hasPayload && TryGetInt(payload, "code", out var innerCode)
                ? innerCode
                : outer.Code == 0
                    ? RawSuccess
                    : outer.Code;

            var message = ResolveMessage(rawStatus, hasPayload ? TryGetString(payload, "message") : outer.Message);
            var state = MapState(rawStatus);

            if (state == LoginState.LoggedIn)
            {
                // 200 + code 0 的路径：Cookie 同样在响应头里。
                PublishCredentials(setCookies);
            }

            result = new QrStatusResult(state, rawStatus, message);
        }

        if (publishState)
        {
            switch (result.State)
            {
                case LoginState.LoggedIn:
                    SetState(LoginState.LoggedIn, result.Message, profile: null);
                    break;
                case LoginState.Expired:
                    SetState(LoginState.Expired, result.Message, profile: null);
                    break;
                case LoginState.WaitingForConfirm:
                    SetState(LoginState.WaitingForConfirm, result.Message, profile: null);
                    break;
                default:
                    SetState(LoginState.WaitingForScan, result.Message, profile: null);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// 登录成功后的收尾：确保凭据已落地、抓取用户信息、触发登录事件。
    /// </summary>
    /// <param name="session">扫码会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>终局结果。</returns>
    private async Task<QrLoginResult> CompleteLoginAsync(
        QrSession session,
        CancellationToken cancellationToken)
    {
        // 同一个 key 只完成一次：重入的轮询不该重复触发登录事件。
        if (_processedKeys.Contains(session.Key))
        {
            return _credentialService.Current is null
                ? new QrLoginResult(QrLoginOutcome.Expired, null, "该二维码已使用，请重新获取")
                : new QrLoginResult(QrLoginOutcome.Succeeded, CurrentProfile, "登录成功");
        }

        _processedKeys.Enqueue(session.Key);

        // 凭据只从 Cookie 容器取：302 与 200 两条路径都已经把 Set-Cookie 写进容器了。
        BiliLoginCredentials? credentials = CaptureCredentialsFromCookieContainer();

        if (credentials is null || !credentials.IsComplete)
        {
            // 302 路径下 Set-Cookie 一定存在；这是"容器与响应头都没拿到"的异常兜底。
            _logger.LogError("扫码成功但未能解析出登录 Cookie（响应缺少 Set-Cookie？）");
            return Fail(LoginState.Failed, "登录失败：未能获取登录凭据，请重试", QrLoginOutcome.NetworkFailed);
        }

        // 落盘失败不回滚已成立的登录：用户手机上确实已经确认过了，交给调用方按日志排查。
        await SaveCredentialSafelyAsync(credentials, cancellationToken).ConfigureAwait(false);

        // 资料接口失败不影响"登录已成立"这一事实：凭据已经拿到了。
        BiliUserProfile? profile = null;
        try
        {
            profile = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "登录成功但用户信息拉取失败，UI 可稍后重试");
        }

        if (profile is null && _credentialService.Current is null)
        {
            // GetProfileAsync 拿到 -101 时会判定登录态失效并清空凭据：此时不能对外宣称"登录成功"。
            _logger.LogWarning("扫码已确认，但登录态校验未通过（nav 返回未登录）");
            ClearSession();
            ClearPersistedCredentialsSafely();
            return Fail(LoginState.Expired, "登录未通过校验，请重新扫码", QrLoginOutcome.Expired);
        }

        _logger.LogInformation("扫码登录成功：mid={Mid}", profile?.Mid ?? 0);
        SetState(LoginState.LoggedIn, "登录成功", profile);
        RaiseLoginSucceeded(profile);

        return new QrLoginResult(QrLoginOutcome.Succeeded, profile, "登录成功");
    }

    /// <summary>
    /// 保存凭据并落盘；失败只记日志，不改变"登录已成立"这个结论。
    /// </summary>
    /// <param name="credentials">本次登录取得的凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task SaveCredentialSafelyAsync(
        BiliLoginCredentials credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            await _credentialService.SaveAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "登录已成立但凭据未能落盘，下次启动将无法自动登录");
        }
    }

    /// <summary>
    /// 清理本地的凭据副本；失败只记日志——清理是登录态失效的附属动作，不该反客为主。
    /// </summary>
    private void ClearPersistedCredentialsSafely()
    {
        try
        {
            _credentialService.ClearAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "凭据副本清理失败，本地可能仍留有等价于登录态的文件");
        }
    }

    /// <summary>
    /// 构造一个失败终局并把状态推进到对应终态（状态与事件通知只在这一处发生）。
    /// </summary>
    /// <param name="state">要推进到的终态。</param>
    /// <param name="message">面向用户的说明文本。</param>
    /// <param name="outcome">终局类型。</param>
    /// <returns>终局结果。</returns>
    private QrLoginResult Fail(LoginState state, string message, QrLoginOutcome outcome)
    {
        SetState(state, message, profile: null);
        return new QrLoginResult(outcome, null, message);
    }

    /// <summary>
    /// 从响应的 <c>Set-Cookie</c> 中抽取 Cookie 并写入 Cookie 容器。
    /// </summary>
    /// <param name="setCookies">响应携带的 Set-Cookie 原始值。</param>
    private void PublishCredentials(IReadOnlyList<string> setCookies)
    {
        foreach (var raw in setCookies)
        {
            if (SplitSetCookie(raw) is { } pair)
            {
                SetCookie(pair.Key, pair.Value);
            }
        }
    }

    /// <summary>
    /// 从 Cookie 容器里取凭据；容器里已有本轮登录写入的 Cookie（见 <see cref="PublishCredentials"/>），
    /// 所以这是取凭据的唯一入口，不必再区分"响应头给的"与"容器本来就有"。
    /// </summary>
    /// <returns>三个关键 Cookie 齐备时返回凭据，否则返回 <see langword="null"/>。</returns>
    private BiliLoginCredentials? CaptureCredentialsFromCookieContainer()
    {
        var cookieHeader = _cookies.GetCookieHeader(new Uri(ApiOrigin));
        if (string.IsNullOrEmpty(cookieHeader))
        {
            return null;
        }

        return BuildCredentials(ParseCookiePairs(cookieHeader));
    }

    /// <summary>
    /// 依据 Cookie 键值对组装凭据。
    /// </summary>
    /// <param name="pairs">Cookie 键值对。</param>
    /// <returns>凭据；三个关键 Cookie 不齐备时返回 <see langword="null"/>。</returns>
    private static BiliLoginCredentials? BuildCredentials(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var sessData = Find(pairs, "SESSDATA");
        var biliJct = Find(pairs, "bili_jct");
        var dedeUserId = Find(pairs, "DedeUserID");

        if (string.IsNullOrEmpty(sessData) || string.IsNullOrEmpty(biliJct) || string.IsNullOrEmpty(dedeUserId))
        {
            return null;
        }

        var header = string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"));

        return new BiliLoginCredentials(sessData, biliJct, dedeUserId, header);

        static string Find(IReadOnlyList<KeyValuePair<string, string>> source, string name)
            => source.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
    }

    /// <summary>
    /// 解析单个 <c>Set-Cookie</c> 头的 <c>name=value</c> 部分（value 中可能含 <c>=</c>）。
    /// </summary>
    /// <param name="setCookie">Set-Cookie 原始值。</param>
    /// <returns>首个键值对；无法解析时返回 <see langword="null"/>。</returns>
    private static KeyValuePair<string, string>? SplitSetCookie(string setCookie)
    {
        var firstSegment = setCookie.Split(';')[0];
        var separator = firstSegment.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        var name = firstSegment[..separator].Trim();
        var value = firstSegment[(separator + 1)..].Trim();
        return name.Length == 0 ? null : new KeyValuePair<string, string>(name, value);
    }

    /// <summary>
    /// 解析 Cookie 头为键值对。
    /// </summary>
    /// <param name="cookieHeader">形如 <c>a=1; b=2</c> 的 Cookie 头。</param>
    /// <returns>键值对列表。</returns>
    private static List<KeyValuePair<string, string>> ParseCookiePairs(string cookieHeader)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        foreach (var segment in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            pairs.Add(new KeyValuePair<string, string>(segment[..separator].Trim(), segment[(separator + 1)..].Trim()));
        }

        return pairs;
    }

    /// <summary>
    /// 把 Cookie 写入容器。域名统一用 <c>bilibili.com</c>：容器会自行派生到各个子域，
    /// 而带 <c>passport.bilibili.com</c> 域的 Cookie 不会发往 <c>api.bilibili.com</c>。
    /// </summary>
    /// <param name="name">Cookie 名。</param>
    /// <param name="value">Cookie 值。</param>
    private void SetCookie(string name, string value)
    {
        try
        {
            _cookies.Add(new Cookie(name, value, "/", "bilibili.com"));
        }
        catch (CookieException ex)
        {
            _logger.LogWarning(ex, "Cookie {Name} 格式非法，已跳过", name);
        }
    }

    /// <summary>
    /// 读取响应中的全部 <c>Set-Cookie</c>。
    /// </summary>
    /// <param name="response">HTTP 响应。</param>
    /// <returns>Set-Cookie 原始值列表。</returns>
    private static List<string> ReadSetCookies(HttpResponseMessage response)
    {
        var result = new List<string>();
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            result.AddRange(values);
        }

        return result;
    }

    /// <summary>
    /// 发起请求：统一补齐 Referer / Origin / UA，并按需附加 Cookie 头与超时。
    /// </summary>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="uri">请求地址。</param>
    /// <param name="referer">Referer 取值（登录域与内容域不同）。</param>
    /// <param name="cookieHeader">要显式附加的 Cookie 头；<see langword="null"/> 时由 Cookie 容器自动携带。</param>
    /// <param name="timeout">单次请求超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>HTTP 响应（由调用方释放）。</returns>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        string referer,
        string? cookieHeader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Referrer = new Uri(referer);
        request.Headers.TryAddWithoutValidation("Origin", referer);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 延迟到下一次轮询；延迟期间响应取消。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private static Task DelayBeforeNextPollAsync(CancellationToken cancellationToken)
        => Task.Delay(PollInterval, cancellationToken);

    /// <summary>
    /// 把响应体读成 <see cref="JsonDocument"/>；内容不是合法 JSON 时抛出业务异常（而不是让 <c>JsonException</c> 冒到 UI）。
    /// </summary>
    /// <param name="response">HTTP 响应。</param>
    /// <param name="path">接口路径，仅用于异常信息。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>JSON 文档（由调用方释放）。</returns>
    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseJson(body, path);
    }

    /// <summary>
    /// 解析 JSON 文本；失败时抛出业务异常。
    /// </summary>
    /// <param name="body">响应体文本。</param>
    /// <param name="path">接口路径，仅用于异常信息。</param>
    /// <returns>JSON 文档（由调用方释放）。</returns>
    /// <exception cref="BiliApiException">内容不是合法 JSON。</exception>
    private static JsonDocument ParseJson(string body, string path)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new BiliApiException(-1, "响应不是合法 JSON（可能被风控拦截或网络被劫持）", path)
            {
                Source = ex.Source,
            };
        }
    }

    /// <summary>
    /// 读取统一响应结构 <c>{ code, message, ttl, data }</c>。
    /// </summary>
    /// <param name="root">响应 JSON 根元素。</param>
    /// <param name="path">接口路径，仅用于异常信息。</param>
    /// <returns>外层码、提示文本与 <c>data</c> 元素。</returns>
    private static ApiResult ReadApiResult(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ApiResult(-1, $"{path} 响应结构异常", null);
        }

        var code = TryGetInt(root, "code", out var parsedCode) ? parsedCode : -1;
        var message = TryGetString(root, "message") ?? string.Empty;
        var data = root.TryGetProperty("data", out var dataElement) ? dataElement : (JsonElement?)null;

        return new ApiResult(code, message, data);
    }

    /// <summary>
    /// 读取整数字段。
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">读到的值。</param>
    /// <returns>字段存在且是数字时为 <see langword="true"/>。</returns>
    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false,
        };
    }

    /// <summary>
    /// 读取长整数字段（<c>mid</c> 可能超出 int 范围，且接口偶尔以字符串下发）。
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">字段名。</param>
    /// <param name="value">读到的值。</param>
    /// <returns>字段存在且是数字时为 <see langword="true"/>。</returns>
    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(property.GetString(), out value),
            _ => false,
        };
    }

    /// <summary>
    /// 读取字符串字段。
    /// </summary>
    /// <param name="element">父对象。</param>
    /// <param name="name">字段名。</param>
    /// <returns>字段值；不存在或不是字符串时返回 <see langword="null"/>。</returns>
    private static string? TryGetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>
    /// 把状态码映射为登录状态。未知码按"等待扫码"处理（继续轮询），不会误判为成功或失效。
    /// </summary>
    /// <param name="rawStatus">B 站原始状态码。</param>
    /// <returns>归一化状态。</returns>
    private static LoginState MapState(int rawStatus) => rawStatus switch
    {
        RawSuccess => LoginState.LoggedIn,
        RawNotConfirmed => LoginState.WaitingForConfirm,
        RawExpired => LoginState.Expired,
        RawNotScanned => LoginState.WaitingForScan,
        _ => LoginState.WaitingForScan,
    };

    /// <summary>
    /// 生成面向用户的提示文本：优先用接口返回的 message，为空时（B 站常返回 "0"）按状态码兜底。
    /// </summary>
    /// <param name="rawStatus">B 站原始状态码。</param>
    /// <param name="message">接口返回的提示文本，可为空。</param>
    /// <returns>可直接展示的纯文本。</returns>
    private static string ResolveMessage(int rawStatus, string? message)
    {
        var cleaned = string.IsNullOrWhiteSpace(message) ? string.Empty : HtmlResidue.Replace(message, " ").Trim();

        return cleaned.Length == 0 || cleaned == "0"
            ? rawStatus switch
            {
                RawNotScanned => "请使用哔哩哔哩手机客户端扫描二维码",
                RawNotConfirmed => "扫码成功，请在手机上确认登录",
                RawSuccess => "登录成功",
                RawExpired => "二维码已失效，请重新获取",
                _ => $"未知状态（{rawStatus}）",
            }
            : cleaned;
    }

    /// <summary>
    /// 读取用户信息字段。
    /// </summary>
    /// <param name="data">nav 接口的 <c>data</c> 对象。</param>
    /// <returns>用户信息；缺少关键字段时返回 <see langword="null"/>。</returns>
    private static BiliUserProfile? ReadProfile(JsonElement data)
    {
        var mid = TryGetLong(data, "mid", out var parsedMid) ? parsedMid : 0;
        var nickname = TryGetString(data, "uname");
        if (mid <= 0 || string.IsNullOrEmpty(nickname))
        {
            return null;
        }

        var vipStatus = TryGetInt(data, "vipStatus", out var parsedVip) ? parsedVip : 0;

        var level = 0;
        if (data.TryGetProperty("level_info", out var levelInfo) &&
            levelInfo.ValueKind == JsonValueKind.Object &&
            TryGetInt(levelInfo, "current_level", out var currentLevel))
        {
            level = currentLevel;
        }

        return new BiliUserProfile(mid, nickname, ToHttpsUri(TryGetString(data, "face")), vipStatus == 1, level);
    }

    /// <summary>
    /// 把 <c>//</c> 协议相对或 <c>http://</c> 地址统一升为 <c>https://</c>，避免混合内容被拦截。
    /// </summary>
    /// <param name="url">原始地址。</param>
    /// <returns>绝对 https 地址；无法解析时返回 <see langword="null"/>。</returns>
    private static Uri? ToHttpsUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalized = url.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + url
            : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + url["http://".Length..]
                : url;

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>
    /// 判断状态码是否属于重定向（登录成功的另一种表现形态）。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <returns>是重定向时为 <see langword="true"/>。</returns>
    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// 更新状态并在发生变化时触发 <see cref="StateChanged"/>。
    /// </summary>
    /// <param name="state">新状态。</param>
    /// <param name="message">说明文本。</param>
    /// <param name="profile">用户信息。</param>
    private void SetState(LoginState state, string message, BiliUserProfile? profile)
    {
        var text = string.IsNullOrWhiteSpace(message) ? ResolveMessage(-1, null) : message;

        lock (_gate)
        {
            _state = state;
        }

        if (state is LoginState.Idle)
        {
            _logger.LogDebug("登录状态 → {State}", state);
        }
        else
        {
            _logger.LogInformation("登录状态 → {State}：{Message}", state, text);
        }

        var handler = StateChanged;
        if (handler is not null)
        {
            handler(this, new LoginStateChangedEventArgs(state, text, profile));
        }
    }

    /// <summary>
    /// 触发登录成功事件。
    /// </summary>
    /// <param name="profile">用户信息。</param>
    private void RaiseLoginSucceeded(BiliUserProfile? profile)
    {
        var handler = LoginSucceeded;
        if (handler is not null)
        {
            handler(this, new LoginSucceededEventArgs(profile));
        }
    }

    /// <summary>
    /// 清空内存中的用户信息；凭据的清理走 <see cref="ICredentialService"/>。
    /// </summary>
    private void ClearSession()
    {
        lock (_gate)
        {
            _profile = null;
        }
    }

    /// <summary>
    /// 创建内部使用的 <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="handler">HTTP 传输。</param>
    /// <returns>HTTP 客户端。</returns>
    private static HttpClient CreateClient(HttpMessageHandler handler)
        // 超时由每次请求自己的 CancellationTokenSource 控制：轮询与取码的超时要求不同。
        => new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 抛出已释放异常。
    /// </summary>
    /// <exception cref="ObjectDisposedException">本实例已释放。</exception>
    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// 统一响应结构的外层部分。
    /// </summary>
    /// <param name="Code">外层业务码。</param>
    /// <param name="Message">外层提示文本。</param>
    /// <param name="Data">载荷元素。</param>
    private readonly record struct ApiResult(int Code, string Message, JsonElement? Data);
}
