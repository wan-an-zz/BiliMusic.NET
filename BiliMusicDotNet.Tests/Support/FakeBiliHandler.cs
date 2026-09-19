using System.Net;
using System.Text;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 假 HTTP 传输：按 URL 决定响应，并记录每次请求的可观测特征（地址、Referer、Cookie、UA）。
/// </summary>
/// <remarks>
/// 被测服务接受注入的 <see cref="HttpMessageHandler"/>，因此无需联网也不需要真实账号
/// 即可驱动完整登录流程——这正是把这些特征断言变成回归测试的前提。
/// </remarks>
public sealed class FakeBiliHandler(Func<string, HttpResponseMessage> responder) : HttpMessageHandler
{
    /// <summary>按到达顺序记录的请求。</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>轮询接口被调用的次数，用于断言"该停的时候真的停了"。</summary>
    public int PollCount { get; private set; }

    /// <summary>联网失败模式下抛出的异常（模拟断网 / DNS 失败）。</summary>
    public Exception? ThrowOnSend { get; init; }

    /// <summary>响应规则；运行中可替换，用于模拟"同一会话里登录态后来失效了"。</summary>
    public Func<string, HttpResponseMessage> Responder { get; set; } = responder;

    /// <summary>构造一个"登录成功"的传输：取码 → 302 + Set-Cookie → nav 返回用户信息。</summary>
    /// <param name="navBody">nav 接口的响应体，默认返回已登录。</param>
    /// <returns>假传输。</returns>
    public static FakeBiliHandler Succeeding(string? navBody = null) => new(url => url.Contains("generate")
        ? Responses.Json(Responses.GenerateBody)
        : url.Contains("/qrcode/poll")
            ? Responses.Redirect(Responses.SetCookies)
            : Responses.Json(navBody ?? Responses.NavLoggedIn));

    /// <summary>构造一个"取码成功、轮询始终未扫码"的传输。</summary>
    /// <returns>假传输。</returns>
    public static FakeBiliHandler Waiting() => new(url => url.Contains("generate")
        ? Responses.Json(Responses.GenerateBody)
        : Responses.Json(Responses.PollBody(Responses.StatusNotScanned)));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.AbsoluteUri;

        Requests.Add(new CapturedRequest(
            request.RequestUri!,
            Header(request, "Referer") ?? request.Headers.Referrer?.ToString(),
            Header(request, "Cookie"),
            Header(request, "User-Agent")));

        if (uri.Contains("/qrcode/poll"))
        {
            PollCount++;
        }

        return ThrowOnSend is not null
            ? Task.FromException<HttpResponseMessage>(ThrowOnSend)
            : Task.FromResult(Responder(uri));
    }

    // Referer 由 Headers.Referrer 写入、Cookie/UA 走 TryAddWithoutValidation，
    // 两者存放位置不同，这里统一从请求头读一次。
    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;
}

/// <summary>一次被记录的请求。</summary>
/// <param name="Uri">请求地址。</param>
/// <param name="Referer">Referer 请求头。</param>
/// <param name="Cookie">Cookie 请求头（由 CookieContainer 或本服务显式附加）。</param>
/// <param name="UserAgent">UA 请求头。</param>
public sealed record CapturedRequest(Uri Uri, string? Referer, string? Cookie, string? UserAgent);

/// <summary>预置响应体与响应对象。</summary>
public static class Responses
{
    /// <summary>测试用二维码内容。</summary>
    public const string QrUrl = "https://www.bilibili.com/h5/login?qrcode_key_demo";

    /// <summary>测试用轮询凭据。</summary>
    public const string QrKey = "demo-qrcode-key";

    /// <summary>B 站扫码状态：未扫码。</summary>
    public const int StatusNotScanned = 86101;

    /// <summary>B 站扫码状态：已扫码待确认。</summary>
    public const int StatusNotConfirmed = 86090;

    /// <summary>B 站扫码状态：登录成功。</summary>
    public const int StatusSuccess = 0;

    /// <summary>B 站扫码状态：二维码已失效。</summary>
    public const int StatusExpired = 86038;

    /// <summary>登录成功时下发的 4 个关键 Cookie（含一个值里带 '=' 的，用于验证切分）。</summary>
    public const string SetCookies =
        "SESSDATA=demo-sessdata-value; Path=/; Domain=.bilibili.com; HttpOnly, " +
        "bili_jct=demo-jct-value; Path=/; Domain=.bilibili.com, " +
        "DedeUserID=123456; Path=/; Domain=.bilibili.com, " +
        "DedeUserID__ckMd5=abcdef0123456789; Path=/; Domain=.bilibili.com";

    /// <summary>取码成功响应。</summary>
    public static string GenerateBody =>
        $$$"""{"code":0,"message":"0","ttl":1,"data":{"url":"{{{QrUrl}}}","qrcode_key":"{{{QrKey}}}"}}""";

    /// <summary>nav 已登录响应（头像故意用 // 协议相对地址）。</summary>
    public const string NavLoggedIn = """
        {"code":0,"message":"0","ttl":1,"data":{"isLogin":true,"mid":123456,"uname":"测试用户","face":"//i0.hdslb.com/bfs/face/demo.jpg","vipStatus":1,"level_info":{"current_level":6}}}
        """;

    /// <summary>nav 未登录响应（<c>-101</c> 是登录态失效的判定依据）。</summary>
    public const string NavNotLoggedIn = """
        {"code":-101,"message":"账号未登录","ttl":1,"data":{"isLogin":false}}
        """;

    /// <summary>轮询响应：状态码在<b>内层</b> data.code。</summary>
    /// <param name="status">B 站扫码状态码。</param>
    /// <param name="message">内层提示文本。</param>
    /// <returns>JSON 文本。</returns>
    public static string PollBody(int status, string message = "0") =>
        $$$"""{"code":0,"message":"0","ttl":1,"data":{"url":"","qrcode_key":"{{{QrKey}}}","code":{{{status}}},"message":"{{{message}}}"}}""";

    /// <summary>构造 JSON 响应，可附带 Set-Cookie。</summary>
    /// <param name="body">响应体。</param>
    /// <param name="setCookie">Set-Cookie 原始值，多个之间用 ", " 分隔。</param>
    /// <returns>HTTP 响应。</returns>
    public static HttpResponseMessage Json(string body, string? setCookie = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AddSetCookies(response, setCookie);
        return response;
    }

    /// <summary>构造 302 响应（登录成功的另一种表现形态，响应体为空）。</summary>
    /// <param name="setCookie">Set-Cookie 原始值。</param>
    /// <returns>HTTP 响应。</returns>
    public static HttpResponseMessage Redirect(string setCookie)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        AddSetCookies(response, setCookie);
        return response;
    }

    /// <summary>构造二进制响应（头像下载用）。</summary>
    /// <param name="body">响应体字节。</param>
    /// <returns>HTTP 响应。</returns>
    public static HttpResponseMessage Bytes(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static void AddSetCookies(HttpResponseMessage response, string? setCookie)
    {
        if (string.IsNullOrEmpty(setCookie))
        {
            return;
        }

        foreach (var one in setCookie.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            response.Headers.TryAddWithoutValidation("Set-Cookie", one);
        }
    }
}
