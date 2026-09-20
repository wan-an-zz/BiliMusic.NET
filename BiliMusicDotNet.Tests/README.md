# BiliMusicDotNet.Tests

登录服务（`BiliLoginService` / `IBiliLoginService`）与凭据服务（`CredentialService` / `ICredentialService`）的 xUnit 测试。

## 怎么跑

```powershell
# 唯一的权威入口：编译并运行测试程序集，进程退出码即测试结果（0 = 全绿）
dotnet run --project .\BiliMusicDotNet.Tests\BiliMusicDotNet.Tests.csproj
```

输出末尾的汇总行是判定依据：

```
=== TEST EXECUTION SUMMARY ===
   BiliMusicDotNet.Tests  Total: 49, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 7.7s
```

**提交前必须全绿（`Failed: 0` 且 `Errors: 0`）**，这是本仓库的提交闸门。

### 为什么不用 `dotnet test`

测试项目用 **xunit.v3**，它自带 Microsoft.Testing.Platform（MTP）运行器，在测试程序集内进程内执行。
而 `dotnet test` 在本机 .NET 10 SDK（10.0.111）上仍走 VSTest 路径，需要 `testhost` 子进程——
该子进程会调用 `Process.GetProcessHandle` 打开父进程句柄，本机沙箱直接拒绝访问（Win32 错误 5），
测试运行必然中止。直接 `dotnet run` 绕开了这条路径，行为与 MTP 完全一致。

联网环境且 `Microsoft.Testing.Platform` 升级到支持 SDK 10 的版本后，可以改回 `dotnet test`。

## 测试怎么做的

被测服务接受注入的 `HttpMessageHandler`，所以整套测试**不联网、不需要 B 站账号**：
`Support/FakeBiliHandler.cs` 按 URL 返回预置响应（含 302 + `Set-Cookie`），
并记录每次请求的地址、`Referer`、`Cookie`、UA 供断言。

凭据持久化同样不碰真实用户目录：`Support/TempDirectory.cs` 给每个用例一个临时应用数据根目录，
`Support/FakeCredentialStore.cs` 则是内存替身（可预置"磁盘上有什么"、可配置落盘失败），供不关心文件的用例使用。

| 测试文件 | 覆盖的子功能 |
| --- | --- |
| `QrSessionTests.cs` | 取码：请求特征、二维码内容/轮询凭据返回、业务码非 0、脏响应 |
| `QrPollingTests.cs` | 轮询：内层状态码映射、四种终止出口、进度回调、失败容忍 |
| `UserProfileTests.cs` | 用户信息：字段解析、Cookie 域、资料接口失败容错、事件载荷 |
| `AvatarTests.cs` | 头像下载：字节返回、CDN 所需 Referer、403 与网络异常 |
| `SessionLifecycleTests.cs` | 登录态恢复（读盘、校验、无凭据时不发请求）、登出、资源释放 |
| `CredentialStorageTests.cs` | 凭据读写：落盘字段、读回写进 `Current`、损坏与不完整兜底、清理幂等、写盘失败 |
| `CredentialPersistenceTests.cs` | 登录/登出/失效与持久化的联动、恢复登录态读盘、落盘失败不阻断登录 |

关键回归点（都是"写错了不会崩、只会静默不对"的地方）：

- 轮询状态必须读**内层** `data.code`（外层恒为 0，读错会立刻误判成登录成功）。
- 登录成功有 **302** 与 **200 + code 0** 两种形态，两条路径都要能拿到 Cookie。
- `.bilibili.com` 的 Cookie 必须能发到 `api.bilibili.com`（否则登录成功但取不到昵称头像）。
- 取消令牌必须能中断轮询（否则用户关掉界面后仍在空转）。
- 登录成功必须留下凭据文件，登出与 `nav` 返回 `-101` 时必须把磁盘副本一起删掉。
- `TryRestoreSessionAsync` 必须自己读盘：磁盘上没凭据时返回 `false` 且**一个请求都不发**。
- 落盘失败不能让已经成立的登录变成失败（那条路径有过一次真实回归）。

## 离线还原

本目录自带 `NuGet.config`，指向仓库内被忽略的离线包源 `.nuget-offline-feed/`，
使 `dotnet run` 在无网络环境也能还原依赖。该目录由脚本生成：

```powershell
pwsh -File .\BiliMusicDotNet.Tests\rebuild-offline-feed.ps1
```

联网环境下删掉本目录的 `NuGet.config` 即改走官方源。
