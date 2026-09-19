using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Exceptions;
using BiliMusicDotNet.Models.Login;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Services;

/// <summary>
/// <see cref="ICredentialService"/> 的默认实现：单个明文 JSON 文件，只保存当前账号。
/// </summary>
/// <remarks>
/// 写入先落临时文件再原子替换，避免进程中断留下半个文件；读取失败一律返回空值，由调用方决定是否重新登录。
/// </remarks>
public sealed class CredentialService : ICredentialService
{
    /// <summary>应用数据目录名，凭据放在它的子目录里，与日志等其它数据隔离。</summary>
    private const string AppFolderName = "BiliMusicDotNet";

    /// <summary>凭据子目录名。</summary>
    private const string CredentialFolderName = "LoginCredential";

    /// <summary>凭据文件名。</summary>
    private const string CredentialFileName = "credentials.json";

    /// <summary>序列化选项。源生成类型在默认构造里按需创建，避免每次读写都重建。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(CredentialJsonContext.Default.Options)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<CredentialService> _logger;
    private readonly string _filePath;
    private BiliLoginCredentials? _current;

    /// <summary>
    /// 供 XAML 设计器与无容器场景实例化；运行期一律由依赖注入提供日志器。
    /// </summary>
    public CredentialService() : this(null, NullLogger<CredentialService>.Instance)
    {
    }

    /// <summary>
    /// 由容器创建：日志器由依赖注入提供，路径用操作系统约定的应用数据目录。
    /// </summary>
    /// <param name="logger">日志器。</param>
    public CredentialService(ILogger<CredentialService> logger) : this(null, logger)
    {
    }

    /// <summary>
    /// 完整构造：显式指定应用数据根目录（测试用它避开真实用户目录）。
    /// </summary>
    /// <param name="baseDirectory">
    /// 应用数据根目录；<see langword="null"/> 时用 <see cref="Environment.SpecialFolder.LocalApplicationData"/>。
    /// </param>
    /// <param name="logger">日志器。</param>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> 是空白字符串。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> 为 <see langword="null"/>。</exception>
    public CredentialService(string? baseDirectory, ILogger<CredentialService> logger)
    {
        if (baseDirectory is not null && string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("应用数据根目录不能是空白字符串", nameof(baseDirectory));
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = ResolveFilePath(baseDirectory ?? DefaultBaseDirectory());
    }

    /// <inheritdoc />
    public BiliLoginCredentials? Current
    {
        get
        {
            lock (this)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// 某个应用数据根目录下凭据文件的完整路径。
    /// </summary>
    /// <param name="baseDirectory">应用数据根目录。</param>
    /// <returns>形如 <c>&lt;baseDirectory&gt;\BiliMusicDotNet\LoginCredential\credentials.json</c> 的路径。</returns>
    public static string ResolveFilePath(string baseDirectory)
        => Path.Combine(baseDirectory, AppFolderName, CredentialFolderName, CredentialFileName);

    /// <inheritdoc />
    public async Task<BiliLoginCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogDebug("凭据文件不存在，视为未登录：{Path}", _filePath);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var credentials = JsonSerializer.Deserialize(json, CredentialJsonContext.Default.BiliLoginCredentials);

            if (credentials is null || !credentials.IsComplete)
            {
                _logger.LogWarning("凭据文件内容不完整，按未登录处理：{Path}", _filePath);
                return null;
            }

            lock (this)
            {
                _current = credentials;
            }

            _logger.LogInformation("已读回登录凭据：mid={Mid}", credentials.DedeUserId);
            return credentials;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 损坏的副本只影响"免登录"，重新扫码即可覆盖，因此不抛异常。
            _logger.LogWarning(ex, "凭据文件无法读取，按未登录处理：{Path}", _filePath);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(BiliLoginCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        lock (this)
        {
            _current = credentials;
        }

        var directory = Path.GetDirectoryName(_filePath)!;
        var temporaryPath = _filePath + ".tmp";

        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(credentials, CredentialJsonContext.Default.BiliLoginCredentials);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            TryDeleteTemporaryFile(temporaryPath);
            _logger.LogError(ex, "凭据写入失败：{Path}", _filePath);
            throw new StorageException("写入", _filePath, ex);
        }

        // 只记 mid 与文件路径：凭据值绝不进日志。
        _logger.LogInformation("登录凭据已保存：mid={Mid}，文件={Path}", credentials.DedeUserId, _filePath);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (this)
        {
            _current = null;
        }

        if (!File.Exists(_filePath))
        {
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "凭据副本删除失败：{Path}", _filePath);
            throw new StorageException("删除", _filePath, ex);
        }

        _logger.LogInformation("凭据副本已删除：{Path}", _filePath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 操作系统约定的应用数据根目录。
    /// </summary>
    /// <returns>根目录路径；平台未提供时退回当前目录。</returns>
    private static string DefaultBaseDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path;
    }

    /// <summary>
    /// 清理写入中断留下的临时文件；清理失败不影响原始异常向上抛出。
    /// </summary>
    /// <param name="temporaryPath">临时文件路径。</param>
    private void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "临时文件清理失败：{Path}", temporaryPath);
        }
    }
}

/// <summary>
/// 凭据的源生成序列化上下文（裁剪与 AOT 友好）。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BiliLoginCredentials))]
internal sealed partial class CredentialJsonContext : JsonSerializerContext;
