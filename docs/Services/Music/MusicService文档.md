# MusicService 文档

音乐播放的唯一入口：播放列表、地址解析、BASS 输出、异步预取与自动切歌都在 `MusicService` 内收口。
调用方只依赖 `IMusicService`，不接触播放地址、BASS 句柄或原生库。

## 1. 技术栈

- .NET 10 / C# latest。
- ManagedBass 4.0.2 + BASS 2.4 原生库（Windows x64、Android 四个 ABI）。
- `Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Logging`。
- xUnit v3.2.2（Microsoft.Testing.Platform，`dotnet run` 运行）。
- B 站公开接口：`/x/web-interface/view`、`/x/player/playurl`。

## 2. 文件布局

| 文件 | 职责 |
| --- | --- |
| `BiliMusicDotNet/Abstracts/IMusicService.cs` | 公开接口 |
| `BiliMusicDotNet/Abstracts/IAudioEngine.cs` | 内部音频引擎接口，测试可替换 |
| `BiliMusicDotNet/Models/Music/MusicTrack.cs` | 曲目模型 |
| `BiliMusicDotNet/Models/Music/MusicStreamInfo.cs` | 解析后的地址与元数据（internal） |
| `BiliMusicDotNet/Models/Music/MusicPlaybackState.cs` | 播放状态 |
| `BiliMusicDotNet/Models/Music/PlaylistInsertPosition.cs` | 插入位置 |
| `BiliMusicDotNet/Models/Music/MusicStateChangedEventArgs.cs` | 状态事件参数 |
| `BiliMusicDotNet/Models/Music/MusicPlaybackFailedEventArgs.cs` | 失败事件参数 |
| `BiliMusicDotNet/Models/Exceptions/MusicPlaybackException.cs` | BASS 失败异常（internal） |
| `BiliMusicDotNet/Services/MusicService.cs` | 播放列表、解析、预取、自动切歌 |
| `BiliMusicDotNet/Services/BassAudioEngine.cs` | BASS 建流、播放控制、结束同步 |
| `BiliMusicDotNet/DependencyInjection/ServiceCollectionExtensions.cs` | `IMusicService` 单例注册 |
| `tools/MusicProbe/` | 真机/离线验证探针 |
| `tools/fetch-bass.ps1` | 原生库下载与部署 |

## 3. 公开 API

~~~csharp
public interface IMusicService : IDisposable
{
    IReadOnlyList<MusicTrack> Playlist { get; }
    MusicTrack? Current { get; }
    MusicPlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }

    event EventHandler<MusicStateChangedEventArgs>? StateChanged;
    event EventHandler<MusicPlaybackFailedEventArgs>? PlaybackFailed;

    Task InsertMusicsAsync(IEnumerable<string> bvs, bool isBeginning = false, CancellationToken ct = default);
    Task ReplacePlaylistAsync(IEnumerable<string> bvs, CancellationToken ct = default);
    Task<bool> SelectMusicAsync(string bvid, CancellationToken ct = default);
    Task<bool> TogglePlayPauseAsync(CancellationToken ct = default);
    Task RemoveMusicsAsync(IEnumerable<string> bvs, CancellationToken ct = default);
    Task<bool> PauseAsync(CancellationToken ct = default);
    Task<bool> ResumeAsync(CancellationToken ct = default);
    Task<bool> PlayNextAsync(CancellationToken ct = default);
    Task<bool> PlayPreviousAsync(CancellationToken ct = default);
    Task<bool> PlayPlaylistMusicAsync(string bvid, CancellationToken ct = default);
    Task<bool> PlayMusicAsync(string bvid, CancellationToken ct = default);
    Task<bool> InsertAndPlayAsync(IEnumerable<string> bvs, PlaylistInsertPosition position, CancellationToken ct = default);
}
~~~

内部方法 `GetPlaybackUrlAsync` 只供服务内部、测试与探针诊断使用，不进入公开接口。

### 3.1 方法语义

| 方法 | 行为 |
| --- | --- |
| `InsertMusicsAsync` | 去重后插入首/尾；已存在的 BV 移动到目标位置；仅当 `Current` 为空时自动播放第一首 |
| `ReplacePlaylistAsync` | 立即停止并释放当前流，清空列表后按输入顺序载入；非空自动播放第一首，空列表停在 `Idle` |
| `SelectMusicAsync` | 只准备流不播放，成功时状态为 `Paused`；BV 不在列表返回 `false` |
| `TogglePlayPauseAsync` | 播放中则暂停，否则继续 |
| `RemoveMusicsAsync` | 删除指定 BV；批量包含当前曲目时抛 `InvalidOperationException`，整批不执行 |
| `PauseAsync` / `ResumeAsync` | 无当前曲目返回 `false`；`Resume` 在流已结束或断流时重建 |
| `PlayNextAsync` / `PlayPreviousAsync` | 首尾不循环，越界返回 `false` |
| `PlayPlaylistMusicAsync` | 选中曲必须在列表内，否则抛 `ArgumentException`；若它是当前曲目的下一首，先 `PlayNext` 再移到第一位，避免重复 `Resume`；若是当前曲目，只移动并继续，不重建流；否则先移到第一位再 `Select + Resume` |
| `PlayMusicAsync` | 不在列表时插入首位；已在列表时移动到首位；当前曲目已在播放则只移动不重启 |
| `InsertAndPlayAsync` | 按 `Beginning/End/AfterCurrent` 插入或移动，然后播放本批第一首 |

## 4. 状态与事件

`MusicPlaybackState`：`Idle`、`Loading`、`Playing`、`Paused`、`Stopped`、`Failed`。

- `StateChanged`：播放列表、当前曲目、状态或元数据变化时触发；参数里的 `Playlist` 是快照。
- `PlaybackFailed`：用户可见的加载/播放失败触发；预取失败只记日志。
- 事件在后台线程、持操作锁期间触发；处理器不要同步回调本服务的方法，UI 需自行转投 UI 线程。

## 5. 播放地址解析

### 5.1 请求流程

1. `GET https://api.bilibili.com/x/web-interface/view?bvid={bvid}`
   - 读取 `data.cid`、`title`、`owner.name`、`duration`。
2. `GET https://api.bilibili.com/x/player/playurl?bvid={bvid}&cid={cid}&qn=80&fnver=0&fnval=1&fourk=1`
   - 读取 `data.durl[0].url`、`backup_url`、`quality`、`format`。

请求头：

- `Referer: https://www.bilibili.com`
- `Origin: https://www.bilibili.com`
- 现代桌面浏览器 UA。
- `Cookie`：仅当 `ICredentialService.Current` 非空且请求发往 `api.bilibili.com` 时附加；BASS 的音频请求不带 Cookie。

### 5.2 为什么用 durl 而不是 DASH

`dash.audio` 返回的是分片 `m4s`（fragmented MP4）。实测 BASS 的 `BASS_StreamCreateURL`：

- 对 DASH `m4s` 直链，无论是否附加 Referer/UA、是否使用 `StreamDownloadBlocks`，都返回 `FileFormat`；
- 同一份文件下载到本地后用 `BASS_StreamCreateFile` 可以正常播放（48 kHz / 双声道）；
- 换成 `durl` 的顺序 MP4 后，URL 流可以直接播放。

因此播放路径选择 `fnval=1` 的 `durl` MP4，并请求 `qn=80`。匿名状态下 B 站会降级到 720P，
其音轨约 212 kbps，接近 DASH 30280 档；代价是下载流里同时包含视频轨，流量高于纯音频。

### 5.3 地址规整与缓存

- `//` 开头补 `https:`；`http://` 升级为 `https://`。
- 主地址与 `backup_url` 去重后保存，主地址无效时依次回退。
- 播放地址缓存默认 5 分钟；并发请求同一 BV 复用同一个 in-flight 任务。
- 缓存过期或失败后重新请求；风控/业务码错误不自动重试。
- 日志只记录 bvid、音质、主机名，不输出完整带签名的 URL。

## 6. 播放列表语义

- BV 去空白后校验；空值抛 `ArgumentException`，集合为 `null` 抛 `ArgumentNullException`。
- 同一 BV 不重复：先把本次输入涉及的 BV 从列表移除，再按输入顺序插入目标位置。
- `isBeginning=true` 插入列表首，输入 `[A, B]` 的最终顺序仍是 `A、B`；否则插入列表尾。
- `AfterCurrent` 插入当前曲目之后；当前为空时退化为首位。
- 当前曲目被移动时 `Current` 不变、播放不中断，只更新位置。
- 自动播放只发生在 `Current == null` 且列表非空时，播放列表第一首。
- 不做循环、不做播完自动删除；列表播完即停，循环由调用方负责。
- 删除当前曲目是禁止操作；批量包含当前曲目时整批不删除。

## 7. 自动切歌

触发链：

~~~text
BASS 播放到流末尾
  → BassAudioEngine 的 SyncFlags.End 回调
  → 引擎抛出 PlaybackEnded(generation)
  → MusicService 投递到线程池
  → HandlePlaybackEndedAsync 获取操作锁
  → 守卫检查
  → 下一首 Select + Resume；尾曲则 Stopped
~~~

守卫条件（全部满足才切歌）：

1. 服务未释放。
2. 事件代次等于引擎当前代次；旧流的迟到事件忽略。
3. 状态为 `Playing`；`Paused`、`Loading`、`Stopped`、`Failed`、`Idle` 都忽略。
4. `Current` 非空且仍在列表中。

行为：

- 存在下一首：走与手动 `PlayNext` 相同的 `SelectCore + ResumeCore` 路径。
- 已是尾曲：状态设为 `Stopped`，不循环、不删除、不回头。
- 下一首加载失败：触发 `PlaybackFailed`，停在 `Stopped` 或 `Failed`，不自动重试。
- 分块流长时间暂停可能被 CDN 断开并触发 `End`；此时状态是 `Paused`，守卫会忽略。
- `ReplacePlaylistAsync` 会先 `Unload` 并使代次自增，旧流的结束事件随后到达也会被忽略。

## 8. BASS 网络流

- `Bass.Init()` 在首次建流时执行；构造函数只创建引擎对象，不初始化声卡。
- 网络参数：`NetTimeOut=8000`、`NetReadTimeOut=15000`、`NetPreBuffer=20`（百分比）。
- 请求串：`URL + CRLF + Referer 头 + CRLF + User-Agent 头 + CRLF`，不使用 Cookie。
- 建流：`Bass.CreateStream(request, offset, BassFlags.StreamDownloadBlocks, null)`。
- 新流创建成功后才释放旧流；创建失败保留旧曲目与旧状态。
- 结束同步：`Bass.ChannelSetSync(handle, SyncFlags.End, 0, callback)`，回调只做代次校验并抛事件。
- `Unload` 释放流并使代次自增；`ReplacePlaylist` 与 `Dispose` 使用。
- 暂停会保存 `Position` 与 `PositionBytes`；恢复时：
  - 流仍可播放：直接 `Play(false)`；
  - 流已结束：用保存的字节偏移重建；偏移重建失败则从头播放并记警告。

## 9. 预取

- 触发点：当前曲目变化、插入/删除/整表替换、指定曲播放移动、上下首、自动切歌后。
- 只预取 `view + playurl` 的解析结果，不预下载音频、不建流。
- 预取结果进入与内部解析相同的缓存；播放下一首时优先复用。
- 删除已预取的下一首、插入当前与下一首之间、整表替换时，取消旧预取并重算。
- 预取失败只记日志；成功后回填标题/UP/时长并触发 `StateChanged`。

## 10. 线程与释放

- 所有公开操作由 `SemaphoreSlim` 串行化。
- `Playlist` 返回快照数组，避免 UI 线程读到正在修改的列表。
- `Dispose` 取消预取、解绑事件、停止并释放流、释放 `HttpClient` 与 BASS；重复调用幂等。
- 释放后公开方法抛 `ObjectDisposedException`。

## 11. 测试

共 232 条测试，其中音乐服务新增 74 条；权威入口：

~~~powershell
dotnet run --project ./BiliMusicDotNet.Tests/BiliMusicDotNet.Tests.csproj -p:UsedAvaloniaProducts=""
~~~

| 测试文件 | 覆盖 |
| --- | --- |
| `MusicUrlTests.cs` | 请求头/Cookie、durl 分片、协议相对地址、业务码/脏响应、缓存/并发/取消、BASS 请求串 |
| `MusicPlaylistTests.cs` | 首尾插入、重复 BV 移动、自动播放边界、当前曲目保护、元数据回填 |
| `MusicReplacePlaylistTests.cs` | 整表替换、停流、去重、空表、迟到结束事件、失败保留新表、预取重置 |
| `MusicPlaybackTests.cs` | 暂停继续、上下首、Select、任意曲目播放、三种插入播放位置、失败回滚、Dispose |
| `MusicPlaylistPlayTests.cs` | 下一首先切后移、非下一首先移后播、当前曲不重启、失败不移动 |
| `MusicAutoAdvanceTests.cs` | 代次/状态守卫、尾曲停止、失败出口、事件路径、连续迟到事件 |
| `MusicPauseResumeTests.cs` | 正常恢复不重建、断流按偏移重建、偏移失败从头播放、暂停不切歌 |
| `MusicPrefetchTests.cs` | 只解析不建流、删除后重算、元数据回填、失败不触发事件、复用缓存 |

测试替身：

- `Support/FakeAudioEngine.cs`：记录 URL/偏移，可模拟建流失败与播完事件。
- `Support/MusicResponses.cs`：view 与 durl 的固定响应。
- `Support/MusicTestKit.cs`：构造服务、轮询等待、按 BV 统计请求。

## 12. 提交映射

| 提交 | 内容 |
| --- | --- |
| `chore(deps): 引入 ManagedBass 依赖` | `Directory.Packages.props`、核心 csproj |
| `feat(music): 新增音乐服务接口与模型` | `IMusicService`、模型、事件参数、内部异常与引擎接口 |
| `feat(music): 实现音乐播放服务与 BASS 引擎` | `MusicService`、`BassAudioEngine`、DI、`InternalsVisibleTo` |
| `build(music): 新增 BASS 原生库获取与部署` | `fetch-bass.ps1`、平台 csproj、`.gitignore` |
| `test(music): 新增音乐服务测试` | 8 个测试文件与 Support 替身 |
| `test(music): 新增音乐播放探针` | `tools/MusicProbe`、README、解决方案项 |
| `docs(music): 新增音乐服务文档` | 本文与测试 README 更新 |

## 13. 最小使用示例

~~~csharp
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddBiliMusicDotNet();

using var provider = services.BuildServiceProvider();
var music = provider.GetRequiredService<IMusicService>();

music.StateChanged += (_, e) =>
    Console.WriteLine($"{e.State}: {e.Current?.Title ?? "(无)"}");

await music.InsertMusicsAsync(["BV1aJaz6yEgq", "BV1cupJ6EEff"]);
await Task.Delay(TimeSpan.FromSeconds(10));
await music.PlayNextAsync();
await music.PauseAsync();
await music.ResumeAsync();
await music.ReplacePlaylistAsync([]);
~~~

需要登录态时，先从 `ICredentialService` 读回凭据再构造/使用服务；未登录时匿名播放可用，但码率可能被降级。

## 14. 原生库部署

~~~powershell
pwsh -File tools/fetch-bass.ps1          # 首次或缺失时
pwsh -File tools/fetch-bass.ps1 -Force   # 强制重新下载
~~~

- Windows：`bass.dll` 复制到 `tools/MusicProbe/native/win-x64` 与 `BiliMusicDotNet.Desktop/native/win-x64`，构建时链接到输出根目录。
- Android：`libbass.so` 与 `libbass_ssl.so` 复制到 `BiliMusicDotNet.Android/native/android/<abi>`，csproj 以 `AndroidNativeLibrary` + `Abi` 元数据打包；已确认 APK 的 `lib/arm64-v8a`、`lib/x86_64` 中存在这两个库。
- Android HTTPS 必须带 `libbass_ssl.so`。
- 原生库目录已在 `.gitignore` 中忽略，二进制不入库。

## 15. 已知限制

- 多 P 稿件使用 view 返回的顶层 cid（默认分 P），暂不支持选择分 P。
- `durl` 对超长视频可能返回多个分片；当前只播放第一个分片，歌曲类稿件通常只有一个。
- `durl` 是视频+音频的复用 MP4，流量高于纯音频 DASH。
- `StreamDownloadBlocks` 下长时间暂停可能被 CDN 断开；恢复会尝试按字节偏移重建，失败则从头播放。
- 不实现 seek、循环、音量、倍速、随机播放。
- 没有内嵌 BASS_AAC 等解码插件；AAC/MP4 依赖系统解码器（Windows Media Foundation / Android 5+）。
- 播放地址缓存 5 分钟，过期后重新请求；B 站签名地址不可长期保存。
