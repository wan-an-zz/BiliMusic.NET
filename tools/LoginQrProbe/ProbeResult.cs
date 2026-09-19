using System.Text;
using BiliMusicDotNet.Models.Login;

namespace BiliMusicDotNet.Tools.LoginQrProbe;

/// <summary>真机扫码验证的断言集合。</summary>
internal static class ProbeAssertions
{
    /// <summary>二维码有效期的合理区间下限。</summary>
    private static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(2);

    /// <summary>二维码有效期的合理区间上限。</summary>
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 判定二维码内容是否是 UI 能渲染的合法地址。
    /// </summary>
    /// <param name="content">二维码内容。</param>
    /// <returns>合法时为 <see langword="true"/>。</returns>
    public static bool IsValidQrContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        if (!Uri.TryCreate(content, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // B 站登录二维码指向 www.bilibili.com 的 h5 登录页，且必须带 qrcode_key。
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase)
            && content.Contains("qrcode_key", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判定二维码有效期是否落在合理区间（实测约 3 分钟）。
    /// </summary>
    /// <param name="session">取码结果。</param>
    /// <returns>合理时为 <see langword="true"/>。</returns>
    public static bool HasReasonableLifetime(QrSession session)
    {
        var lifetime = session.ExpiresAt - DateTimeOffset.Now;
        return lifetime >= MinLifetime && lifetime <= MaxLifetime;
    }
}

/// <summary>真机验证的产物，用于写出报告文件。</summary>
internal sealed class ProbeResult
{
    /// <summary>二维码内容（供人工核对，不含凭据）。</summary>
    public string QrContent { get; init; } = string.Empty;

    /// <summary>二维码失效时刻。</summary>
    public DateTimeOffset QrExpiresAt { get; init; }

    /// <summary>轮询次数。</summary>
    public int PollCount { get; init; }

    /// <summary>登录凭据（仅内存持有，报告里会脱敏）。</summary>
    public BiliLoginCredentials? Credentials { get; init; }

    /// <summary>用户信息。</summary>
    public BiliUserProfile? Profile { get; init; }

    /// <summary>头像字节数。</summary>
    public int AvatarBytes { get; init; }

    /// <summary>断言结论列表。</summary>
    public List<(bool Passed, string Description)> Checks { get; } = [];

    /// <summary>
    /// 生成 Markdown 报告。
    /// </summary>
    /// <returns>报告文本。凭据一律脱敏，绝不写入原值。</returns>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# BiliMusic.NET 扫码登录真机验证报告");
        sb.AppendLine();
        sb.AppendLine($"- 验证时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- 二维码失效时刻：{(QrExpiresAt == default ? "(未取码)" : QrExpiresAt.ToString("HH:mm:ss"))}");
        sb.AppendLine($"- 轮询次数：{PollCount}");
        sb.AppendLine();

        sb.AppendLine("## 断言结论");
        sb.AppendLine();
        if (Checks.Count == 0)
        {
            sb.AppendLine("(无)");
        }
        else
        {
            foreach (var (passed, description) in Checks)
            {
                sb.AppendLine($"- [{(passed ? "x" : " ")}] {description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 用户信息");
        sb.AppendLine();
        if (Profile is null)
        {
            sb.AppendLine("- (未取到)");
        }
        else
        {
            sb.AppendLine($"- mid：{Profile.Mid}");
            sb.AppendLine($"- 昵称：{Profile.Nickname}");
            sb.AppendLine($"- 头像地址：{Profile.AvatarUri}");
            sb.AppendLine($"- 大会员：{(Profile.IsVip ? "是" : "否")}");
            sb.AppendLine($"- 等级：{Profile.Level}");
            sb.AppendLine($"- 头像字节数：{AvatarBytes}");
        }

        sb.AppendLine();
        sb.AppendLine("## 凭据（已脱敏）");
        sb.AppendLine();
        if (Credentials is null)
        {
            sb.AppendLine("- (未取到)");
        }
        else
        {
            sb.AppendLine($"- SESSDATA：{Mask(Credentials.SessData)}");
            sb.AppendLine($"- bili_jct：{Mask(Credentials.BiliJct)}");
            sb.AppendLine($"- DedeUserID：{Mask(Credentials.DedeUserId)}");
            sb.AppendLine($"- 三件套齐备：{(Credentials.IsComplete ? "是" : "否")}");
            sb.AppendLine("- 说明：本报告只记录长度与首尾片段，完整凭据仅存在于内存，未落盘。");
        }

        sb.AppendLine();
        sb.AppendLine("## 二维码内容（供人工核对）");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(QrContent.Length == 0 ? "(无)" : QrContent);
        sb.AppendLine("```");

        return sb.ToString();
    }

    /// <summary>凭据脱敏：只保留长度与首尾各 2 个字符。</summary>
    private static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(空)";
        }

        return value.Length <= 6
            ? $"{Redacted}（长度 {value.Length}）"
            : $"{value[..2]}***{value[^2..]}（长度 {value.Length}）";
    }

    private const string Redacted = "***";
}
