using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiliMusicDotNet.Abstracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// 契约类型是嵌套在接口里的：C# 的嵌套类型不被实现类继承，因此在本文件顶部做一次别名，
// 让下面的实现代码保持可读（对外暴露的类型名仍是 IBiliLoginService.XXX）。
using BiliApiException = BiliMusicDotNet.Abstracts.IBiliLoginService.BiliApiException;
using BiliUserProfile = BiliMusicDotNet.Abstracts.IBiliLoginService.BiliUserProfile;
using LoginState = BiliMusicDotNet.Abstracts.IBiliLoginService.LoginState;
using LoginStateChangedEventArgs = BiliMusicDotNet.Abstracts.IBiliLoginService.LoginStateChangedEventArgs;
using QrSession = BiliMusicDotNet.Abstracts.IBiliLoginService.QrSession;

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
/// <b>不落盘</b>：凭据只保存在内存里，加密持久化属于宿主/UI 层责任，见接口备注。
/// </para>
/// </remarks>
public sealed class BiliLoginService : IBiliLoginService
{
    /// <summary>登录相关接口的域名。</summary>
    private const string PassportOrigin = "https://passport.bilibili.com";

    /// <summary>登录接口要求的 Referer（与内容接口的取值不同，写错会被风控）。</summary>
    private const string LoginReferer = PassportOrigin + "/login";

    /// <summary>现代桌面浏览器 UA：默认 UA 中包含的 "unknown" 特征容易被风控针对。</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    /// <summary>二维码有效期（B 站实测约 3 分钟），既是会话的展示用失效时刻，也是轮询的兜底超时。</summary>
    private static readonly TimeSpan QrCodeLifetime = TimeSpan.FromMinutes(3);

    /// <summary>取码请求的超时（可交互路径，失败要快速反馈给用户）。</summary>
    private static readonly TimeSpan GenerateRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly ILogger<BiliLoginService> _logger;
    private readonly CookieContainer _cookies = new();

    private HttpClient _http;
    private bool _disposed;

    private LoginState _state = LoginState.Idle;

    /// <summary>
    /// 供 XAML 设计器与无容器场景实例化；运行期一律由依赖注入提供日志器。
    /// </summary>
    public BiliLoginService() : this(new HttpClientHandler(), NullLogger<BiliLoginService>.Instance)
    {
    }

    /// <summary>
    /// 由容器创建：日志器由依赖注入提供，HTTP 传输可注入（便于离线单测与代理）。
    /// </summary>
    /// <param name="logger">日志器。</param>
    public BiliLoginService(ILogger<BiliLoginService> logger) : this(new HttpClientHandler(), logger)
    {
    }

    /// <summary>
    /// 完整构造：显式指定 HTTP 传输与日志器。
    /// </summary>
    /// <param name="handler">
    /// HTTP 传输。会被包进 <see cref="HttpClient"/> 后由本服务持有并在 <see cref="Dispose"/> 时释放。
    /// </param>
    /// <param name="logger">日志器。</param>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> 或 <paramref name="logger"/> 为 <see langword="null"/>。</exception>
    public BiliLoginService(HttpMessageHandler handler, ILogger<BiliLoginService> logger)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
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
    /// 更新状态并在发生变化时触发 <see cref="StateChanged"/>。
    /// </summary>
    /// <param name="state">新状态。</param>
    /// <param name="message">说明文本。</param>
    /// <param name="profile">用户信息。</param>
    private void SetState(LoginState state, string message, BiliUserProfile? profile)
    {
        lock (_gate)
        {
            _state = state;
        }

        _logger.LogInformation("登录状态 → {State}：{Message}", state, message);

        var handler = StateChanged;
        if (handler is not null)
        {
            handler(this, new LoginStateChangedEventArgs(state, message, profile));
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
