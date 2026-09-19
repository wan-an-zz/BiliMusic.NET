using System.IO;

namespace BiliMusicDotNet.Tests.Support;

/// <summary>
/// 每个用例独占的临时应用数据根目录；销毁时尽力清理。
/// </summary>
/// <remarks>
/// 持久化服务的默认路径落在真实用户目录，用例必须显式传本目录，
/// 否则测试会把凭据写到开发机自己的 <c>%LOCALAPPDATA%</c> 里。
/// </remarks>
public sealed class TempDirectory : IDisposable
{
    /// <summary>创建一个唯一命名的临时目录。</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BiliMusicDotNet.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    /// <summary>应用数据根目录路径。</summary>
    public string Path { get; }

    /// <summary>
    /// 轮询等待文件出现，用于"落盘是异步的"这类断言，避免依赖固定延时。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <returns>在超时前出现时为 <see langword="true"/>。</returns>
    public static async Task<bool> WaitForFileAsync(string filePath)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (File.Exists(filePath))
            {
                return true;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return File.Exists(filePath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 杀毒软件或索引器可能短暂占用文件，重试几次再放弃。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }
}
