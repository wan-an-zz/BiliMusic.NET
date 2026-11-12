using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>B 站音乐接口的固定响应与测试常量。</summary>
public static class MusicResponses
{
    /// <summary>默认测试 BV。</summary>
    public const string Bv1 = "BV1aJaz6yEgq";

    /// <summary>第二个测试 BV。</summary>
    public const string Bv2 = "BV1cupJ6EEff";

    /// <summary>第三个测试 BV。</summary>
    public const string Bv3 = "BV1xx411c7mD";

    /// <summary>第四个测试 BV。</summary>
    public const string Bv4 = "BV1yy411c7mE";

    /// <summary>默认 cid。</summary>
    public const long Cid = 42435085521;

    /// <summary>默认标题。</summary>
    public const string Title = "离去之原 中文ver.";

    /// <summary>默认 UP 主。</summary>
    public const string Owner = "hanser";

    /// <summary>默认时长（秒）。</summary>
    public const int DurationSeconds = 275;

    /// <summary>默认分片地址。</summary>
    public const string HighUrl = "https://cdn.example/high.mp4";

    /// <summary>默认分片备用地址。</summary>
    public const string HighBackupUrl = "https://backup.example/high.mp4";

    /// <summary>构造 view 成功响应。</summary>
    /// <param name="bvid">BV 号。</param>
    /// <param name="cid">cid。</param>
    /// <param name="title">标题。</param>
    /// <param name="owner">UP 主。</param>
    /// <param name="duration">时长秒数。</param>
    /// <returns>HTTP 响应。</returns>
    public static HttpResponseMessage View(
        string bvid = Bv1,
        long cid = Cid,
        string title = Title,
        string owner = Owner,
        int duration = DurationSeconds)
        => Responses.Json(ViewBody(bvid, cid, title, owner, duration));

    /// <summary>构造 view 响应体。</summary>
    /// <param name="bvid">BV 号。</param>
    /// <param name="cid">cid。</param>
    /// <param name="title">标题。</param>
    /// <param name="owner">UP 主。</param>
    /// <param name="duration">时长秒数。</param>
    /// <returns>JSON 文本。</returns>
    public static string ViewBody(
        string bvid = Bv1,
        long cid = Cid,
        string title = Title,
        string owner = Owner,
        int duration = DurationSeconds)
        => JsonSerializer.Serialize(new
        {
            code = 0,
            message = "0",
            ttl = 1,
            data = new
            {
                bvid,
                cid,
                title,
                owner = new { mid = 1, name = owner },
                duration,
            },
        });

    /// <summary>构造 playurl 成功响应。</summary>
    /// <returns>HTTP 响应。</returns>
    public static HttpResponseMessage PlayUrl() => Responses.Json(PlayUrlBody());

    /// <summary>构造 durl 成功响应。</summary>
    /// <param name="durlJson">可选的 durl 数组文本。</param>
    /// <returns>JSON 文本。</returns>
    public static string PlayUrlBody(string? durlJson = null)
        => "{\"code\":0,\"message\":\"0\",\"ttl\":1,\"data\":{\"quality\":64,\"format\":\"mp4720\",\"durl\":["
           + (durlJson ?? DefaultDurlJson)
           + "]}}";

    /// <summary>默认 durl 分片：带一个备用地址。</summary>
    public static string DefaultDurlJson => JsonSerializer.Serialize(new
    {
        order = 1,
        length = 274133,
        size = 21200000,
        url = HighUrl,
        backup_url = new[] { HighBackupUrl },
    });

    /// <summary>构造一个只按路径分发的响应器。</summary>
    /// <param name="view">view 响应。</param>
    /// <param name="playUrl">playurl 响应。</param>
    /// <returns>响应函数。</returns>
    public static Func<string, HttpResponseMessage> Router(
        HttpResponseMessage? view = null,
        HttpResponseMessage? playUrl = null)
        => uri => uri.Contains("/x/web-interface/view", StringComparison.Ordinal)
            ? view ?? View()
            : playUrl ?? PlayUrl();
}
