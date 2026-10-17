# BiliMusicDotNet.Tests

xUnit 测试，以及基于 Avalonia Headless 的视图 / 视图模型测试。

## 怎么跑

```powershell
# 唯一的权威入口：编译并运行测试程序集，进程退出码即测试结果（0 = 全绿）
dotnet run --project .\BiliMusicDotNet.Tests\BiliMusicDotNet.Tests.csproj
```

输出末尾的汇总行是判定依据：

```
=== TEST EXECUTION SUMMARY ===
   BiliMusicDotNet.Tests  Total: 115, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 7.7s
```

**提交前必须全绿（`Failed: 0` 且 `Errors: 0`）**，这是本仓库的提交闸门。

> 当前有 9 个既有的 UI 弹窗用例为红（`AboutPage*Tests` / `QrLoginViewTests`），是上一次控件库切换留下的，
> 与登录 / 凭据链路无关；判定本条时以"没有新增失败项"为准，基线可用 `git stash -u` 对照。

### 目标框架是 `net10.0-windows`

Windows 端的凭据加密直接用 DPAPI（`System.Security.Cryptography.ProtectedData`）。
标成可移植的 `net10.0` 就得靠抑制 CA1416 才能编译，等于把平台事实藏起来；带 `-windows` 后缀后
分析器保持开启，测试也能直接编译 `DpapiEncryptService` 的源码。

### 为什么不用 `dotnet test`

测试项目用 **xunit.v3**，它自带 Microsoft.Testing.Platform（MTP）运行器，在测试程序集内进程内执行。
而 `dotnet test` 在本机 .NET 10 SDK（10.0.111）上仍走 VSTest 路径，需要 `testhost` 子进程——
该子进程会调用 `Process.GetProcessHandle` 打开父进程句柄，本机沙箱直接拒绝访问（Win32 错误 5），
测试运行必然中止。直接 `dotnet run` 绕开了这条路径，行为与 MTP 完全一致。

联网环境且 `Microsoft.Testing.Platform` 升级到支持 SDK 10 的版本后，可以改回 `dotnet test`。

### xunit 版本必须钉在 3.2.2

`Avalonia.Headless.XUnit` 是针对 **xunit.v3 3.2.2** 编译的。升到 4.x 预发布后，
`TestIntrospectionHelper.GetTestCaseDetails` 的签名变了，`[AvaloniaFact]` 会在**发现阶段**直接抛
`MissingMethodException`（即使 `.xunit-offline-feed` 里躺着 4.x 的包）。要升级 xunit，先确认
`Avalonia.Headless.XUnit` 有对应版本。

## 测试怎么做的

被测服务接受注入的 `HttpMessageHandler`，所以整套测试**不联网、不需要 B 站账号**：
`Support/FakeBiliHandler.cs` 按 URL 返回预置响应（含 302 + `Set-Cookie`），
并记录每次请求的地址、`Referer`、`Cookie`、UA 供断言。

凭据持久化同样不碰真实用户目录：`Support/TempDirectory.cs` 给每个用例一个临时应用数据根目录，
`Support/FakeCredentialStore.cs` 则是内存替身（可预置"磁盘上有什么"、可配置落盘失败），供不关心文件的用例使用。
`Support/FakeEncryptService.cs` 是逐字节异或的可逆替身：它不带平台能力，让共享层测试与探针都能跑。

桌面端的加密实现（`DpapiEncryptService`）靠 `<Compile Include>` 链接进本程序集：
桌面端是带入口点的 WinExe 应用头，为测一个加密类把 Avalonia.Desktop / SukiUI / NLog 全拖进来不划算；
链接的仍是同一份源码，桌面端改了这里就会跟着变。

| 测试文件 | 覆盖的子功能 |
| --- | --- |
| `QrSessionTests.cs` | 取码：请求特征、二维码内容/轮询凭据返回、业务码非 0、脏响应 |
| `QrPollingTests.cs` | 轮询：内层状态码映射、四种终止出口、进度回调、失败容忍 |
| `UserProfileTests.cs` | 用户信息：字段解析、Cookie 域、资料接口失败容错、事件载荷 |
| `AvatarTests.cs` | 头像下载：字节返回、CDN 所需 Referer、403 与网络异常 |
| `SessionLifecycleTests.cs` | 登录态恢复（读盘、校验、无凭据时不发请求）、登出、资源释放 |
| `CredentialStorageTests.cs` | 凭据读写：落盘为密文、交给加密服务的明文、读回写进 `Current`、解不开与不完整兜底、清理幂等、写盘失败、旧明文文件清理 |
| `CredentialPersistenceTests.cs` | 登录/登出/失效与持久化的联动、恢复登录态读盘、落盘失败不阻断登录 |
| `EncryptServiceTests.cs` | 桌面端加密实现（DPAPI）：加解密往返、密文与明文不同、同一明文两次密文不同、陌生字节抛 `CryptographicException`、从流的当前位置读、与凭据服务串起来的端到端链路 |
| `HeadlessHostTests.cs` | Headless 宿主自检：`[AvaloniaFact]` 可用、`avares://` 资产与 `IconAbout` 资源真的能取到 |
| `CountdownFormatTests.cs` | 倒计时文本：`mm:ss` 格式化、不足一秒向上进位、归零即失效 |
| `QrLoginViewModelTests.cs` | 扫码弹窗：加载态判定（不能用 `WaitingForScan` 判）、四类状态文案、倒计时起停与归零、取消后不再响应 |
| `AboutPageViewModelTests.cs` | 「关于」页：默认值、登录成功换头像换名、资料/头像缺失回退、登出恢复默认、删盘失败仍按已登出渲染、取码失败收弹窗 |

### Headless（UI）测试怎么做的

需要 Avalonia 平台（位图、控件、资源）的用例一律标 **`[AvaloniaFact]`**，由
`TestAppBuilder.cs` 起一个真实 `App` 的 Headless 实例（`App.Services` 指向 `HeadlessServices.cs` 里的最小容器，
登录服务换成不落盘、不联网的替身）。纯逻辑用例仍是普通 `[Fact]`。

**坑点**：`AvaloniaHeadlessPlatformOptions.UseHeadlessDrawing` 默认为 `true`，此时位图解码是桩实现，
任何 PNG 的 `PixelSize` 都恒为 `1x1`；`TestAppBuilder` 因此显式关掉它并挂上 `UseSkia()`，
否则二维码尺寸与头像那几条断言会全部失真（而且不会报错，只会静默给出错误值）。

关键回归点（都是"写错了不会崩、只会静默不对"的地方）：

- 轮询状态必须读**内层** `data.code`（外层恒为 0，读错会立刻误判成登录成功）。
- 登录成功有 **302** 与 **200 + code 0** 两种形态，两条路径都要能拿到 Cookie。
- `.bilibili.com` 的 Cookie 必须能发到 `api.bilibili.com`（否则登录成功但取不到昵称头像）。
- 取消令牌必须能中断轮询（否则用户关掉界面后仍在空转）。
- 登录成功必须留下凭据文件，登出与 `nav` 返回 `-101` 时必须把磁盘副本一起删掉。
- `TryRestoreSessionAsync` 必须自己读盘：磁盘上没凭据时返回 `false` 且**一个请求都不发**。
- 落盘失败不能让已经成立的登录变成失败（那条路径有过一次真实回归）。
- 解不开的密文（换机器、密钥失效、文件被改）必须表现为"未登录"，而不是抛异常或返回半份凭据。

## 离线还原

本目录自带 `NuGet.config`，指向两个本地源，使 `dotnet run` 在无网络环境也能还原依赖：

1. `.nuget-offline-feed/`：仓库内被忽略的离线包源，由本机 NuGet 缓存汇集而成；
2. `global-packages`（`$(NuGetPackageRoot)`）：本机 NuGet 缓存目录本身。
   离线源是"逐个包挑出来归档"的，钉住新版本后常缺包；缓存目录凡是本机装过的版本都在，用它兜住缺口。

离线包源由脚本生成：

```powershell
pwsh -File .\BiliMusicDotNet.Tests\rebuild-offline-feed.ps1
```

联网环境下删掉本目录的 `NuGet.config` 即改走官方源。
