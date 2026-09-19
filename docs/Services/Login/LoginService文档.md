# 登录服务（BiliLoginService）维护文档

> 本文档对应"扫码登录"后端业务逻辑的落地过程：流程、契约、验证方式与踩坑记录。
> **本服务不含任何 UI 代码**——二维码渲染、头像显示、界面状态绑定均由 UI 层按本文的契约实现。
>
> 相关文档：[Bilibili API 参考](../../bili_api/bilibili-api-reference.md)（第 7 节"扫码登录流程"）

---

## 1. 技术栈

| 关注点 | 选型 | 说明 |
| --- | --- | --- |
| 语言 / 框架 | .NET 10（`net10.0`）、C# latest | 与主工程一致；文件级命名空间、`record`、主构造函数 |
| HTTP | `System.Net.Http.HttpClient` + `HttpClientHandler` | `CookieContainer` 常驻；**关闭自动重定向**以观察 302 |
| JSON | `System.Text.Json`（`JsonDocument` 手写取值） | 只需读几个字段，不引入 DTO 类；对字段缺失/类型变化更宽容 |
| 日志 | `Microsoft.Extensions.Logging.ILogger<T>`（经 NLog 输出） | 构造函数注入；`nlog.config` 统一决定级别与落盘 |
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` | 由各平台组合根注册；见 §4.1 |
| 异步 / 取消 | `Task` + `CancellationToken` + `CancellationTokenSource.CreateLinkedTokenSource` | 单次请求超时与调用方取消合并处理 |
| 测试 | xUnit v3（`xunit.v3` 4.0.0-pre.81）+ Microsoft.Testing.Platform | 无 VSTest testhost；见 `BiliMusicDotNet.Tests/README.md` |
| 测试替身 | 自定义 `FakeBiliHandler : HttpMessageHandler` | 不引入 Mock 框架；按 URL 返回预置响应并记录请求特征 |

### 涉及的上游接口

| 用途 | 接口 | 域名 | 请求头要点 |
| --- | --- | --- | --- |
| 取码 | `GET /x/passport-login/web/qrcode/generate` | `passport.bilibili.com` | `Referer: https://passport.bilibili.com/login` |
| 轮询扫码状态 | `GET /x/passport-login/web/qrcode/poll?qrcode_key=` | `passport.bilibili.com` | 同上 |
| 用户信息（昵称/头像） | `GET /x/web-interface/nav` | `api.bilibili.com` | `Referer: https://www.bilibili.com` |
| 头像图片 | `GET <face>` | `*.hdslb.com` | `Referer: https://www.bilibili.com`（缺了 403） |

**两个域的 `Referer` 不能混用**，服务内部按接口分别写死。

---

## 2. 子功能、提交与测试

### 2.1 提交对照表

按原子化提交原则拆分，每条提交都自带测试且处于绿灯状态。

| 提交 | 内容 |
| --- | --- |
| `66801ed` `feat(login): 定义登录契约并实现取码` | 契约、取码、状态通知，以及测试项目基础设施（假传输、离线还原、闸门说明） |
| `89bdea3` `feat(login): 实现扫码状态轮询与凭据落地` | 单次状态查询、轮询至终局、凭据解析 + `QrPollingTests` |
| `f408e43` `feat(login): 登录成功后抓取用户昵称与头像地址` | `nav` 取用户信息 + `UserProfileTests` |
| `f624bc2` `feat(login): 下载头像图片字节` | `GetAvatarImageAsync` + `AvatarTests` |
| `d79c522` `feat(login): 支持登录态恢复与登出` | `TryRestoreSessionAsync`、`Logout` + `SessionLifecycleTests` |
| `cf23c12` `docs(login): 补充状态机终态判定与线程约定` | 契约注释：终态判定与线程模型 |
| `5c3c22c` `docs(test): 更新测试项目说明` | 测试项目 README |
| `docs(login): 补登录服务维护文档` | 本文档 |
| `08e21b6` `refactor(login): 异常模型集中到 Models/Exceptions` | `BiliApiException` 迁入 `Models/Exceptions/` |
| `153ebe6` `feat(credential): 新增凭据契约与内存持有者` | `ICredentialService`、`CredentialService` + `CredentialStorageTests` |
| `553182c` `refactor(login): 凭据改由 CredentialService 持有` | 删除 `CurrentCredentials`、登录成功落盘、登出与失效清理 + `CredentialPersistenceTests` |

分支：`feature/Login`。

### 2.2 子功能与测试对照表

测试入口（**提交闸门**）：

```powershell
dotnet run --project .\BiliMusicDotNet.Tests\BiliMusicDotNet.Tests.csproj
# 判定依据：Total: 47, Errors: 0, Failed: 0
```

| # | 子功能 | 实现位置 | 测试 | 用例数 |
| --- | --- | --- | --- | --- |
| 1 | 取码（申请二维码会话） | `CreateQrSessionAsync` | `QrSessionTests` | 7 |
| 2 | 单次状态查询 | `PollQrStatusAsync` | `QrPollingTests.状态查询_*` | 4 |
| 3 | 轮询至终局 | `WaitForConfirmationAsync` | `QrPollingTests.终止_*` / `进度回调_*` | 6 |
| 4 | 凭据组装 | `PublishCredentials` / `BuildCredentials` | `QrPollingTests` 成功用例 | 含在 3 内 |
| 4.1 | 凭据落盘与清理 | `SaveCredentialSafelyAsync` / `ClearPersistedCredentialsSafely` | `CredentialPersistenceTests` | 6 |
| 5 | 用户信息抓取 | `GetProfileAsync` | `UserProfileTests` | 5 |
| 6 | 头像下载 | `GetAvatarImageAsync` | `AvatarTests` | 4 |
| 7 | 登录态恢复 | `TryRestoreSessionAsync` | `SessionLifecycleTests.恢复登录态_*` | 3 |
| 8 | 登出 | `Logout` | `SessionLifecycleTests.登出_*` | 1 |
| 9 | 资源释放 | `Dispose` | `SessionLifecycleTests.释放_*` | 1 |
| 10 | 状态通知 | `StateChanged` / `LoginSucceeded` / `LoggedOut` | 各测试文件均有覆盖 | — |

### 2.3 验证现状（诚实声明）

| 项 | 状态 |
| --- | --- |
| 单元/集成测试（假 HTTP 传输，覆盖全部状态机分支与异常路径） | ✅ 47/47 通过 |
| 真机扫码验证工具 | ✅ 已就绪：`tools/LoginQrProbe`（见 §2.4） |
| 与真实 B 站接口的联调（真机扫码） | ✅ 已执行：工具已备好，需在能访问 `passport.bilibili.com` 的网络里跑一次 |

开发本功能的机器无法访问 `passport.bilibili.com`（TLS 被拦截），
所以"接口形态与文档假设一致"这条只能靠探针在联网环境确认。**接入 UI 前请先跑通 §2.4。**

### 2.4 真机扫码验证探针

```powershell
dotnet run --project tools/LoginQrProbe                 # 真机扫码，终端渲染二维码
dotnet run --project tools/LoginQrProbe -- --proxy http://127.0.0.1:7897
dotnet run --project tools/LoginQrProbe -- --offline    # 离线自检，不联网
```

探针走完整链路并逐步断言，退出码 0 表示全部通过。它重点验证三件单元测试无法确认的事：

| 断言 | 判据出处 |
| --- | --- |
| 二维码内容是合法 bilibili https 登录地址 | §3.1 取码 |
| `nav` 返回昵称 | Cookie 域必须是 `bilibili.com`（服务内部写死，见 §5.5） |
| 头像下载到非空字节 | CDN 请求必须带 `Referer: https://www.bilibili.com`（服务内部写死） |

二维码用 **QRCoder** 生成（`AsciiQRCode` 字符画渲染），首次还原需要联网拉包。
用法、选项与完整断言清单见 `tools/LoginQrProbe/README.md`。

---

## 3. 业务逻辑流程

### 3.1 总览

```
UI（登录界面）                    BiliLoginService                         B 站
    │                                   │                                  │
    │── CreateQrSessionAsync ──────────▶│── GET qrcode/generate ──────────▶│
    │◀── QrSession{Content,Key} ────────│◀── {url, qrcode_key} ────────────│
    │                                   │  State → WaitingForScan          │
    │  （UI 用二维码库渲染 Content）      │  event StateChanged              │
    │                                   │                                  │
    │── WaitForConfirmationAsync ──────▶│                                  │
    │        （可取消）                  │── GET qrcode/poll（1.8s 间隔）──▶│
    │◀── progress: 待扫码/待确认 ────────│◀── data.code = 86101 / 86090 ────│
    │                                   │      event StateChanged          │
    │                                   │        ...                       │
    │                                   │── GET qrcode/poll ──────────────▶│
    │                                   │◀── 302 + Set-Cookie ─────────────│
    │                                   │  解析 SESSDATA/bili_jct/DedeUserID│
    │                                   │── GET nav（带 Cookie）──────────▶│
    │                                   │◀── {uname, face, vipStatus} ─────│
    │◀── QrLoginResult{Succeeded} ──────│  State → LoggedIn                │
    │◀── event LoginSucceeded ──────────│                                  │
    │                                   │                                  │
    │── GetAvatarImageAsync(face) ─────▶│── GET <face>（带 Referer）───────▶│
    │◀── byte[]（UI 解码显示）───────────│                                  │
```

### 3.2 状态机

```
                     CreateQrSessionAsync
        Idle ─────────────────────────────▶ WaitingForScan
                                               │        ▲
                              86090            │        │ 86101
                             ┌─────────────────┘        │
                             ▼                          │
                     WaitingForConfirm ─────────────────┘
                             │            │
                    0        │            │  86038
                             ▼            ▼
                        LoggedIn      Expired ──▶ （重新取码回到 WaitingForScan）
                                          │
                   兜底超时（约 3 分钟）   │   连续网络失败 ≥ 5 次
                             ┌────────────┴────────────┐
                             ▼                         ▼
                          TimedOut                  Failed
                             └──────────┬──────────────┘
                                        │  Logout
                                        ▼
                                       Idle
```

- `LoggedIn` / `Expired` / `TimedOut` / `Failed` 是**终态**，轮询一旦到达即退出；
  判定可用 `IBiliLoginService.IsTerminal(state)`。
- 状态由服务**单方推进**：UI 只读 `State` / 收 `StateChanged`，不自己维护状态机。
- 未知状态码不推进到终态（继续轮询 + 记日志），避免接口改版时误判。

### 3.3 轮询参数（为什么是这些值）

| 项 | 取值 | 理由 |
| --- | --- | --- |
| 轮询间隔 | 1800 ms | 低于 1 秒的请求特征明显偏离正常客户端，易触发风控；对用户感知无收益 |
| 单次请求超时 | 10 s | 超时按**一次失败**处理并继续轮询，不中断整条流程 |
| 连续失败容忍 | 5 次 | 少量网络抖动应重试；持续失败才判定 `NetworkFailed` |
| 兜底超时 | 取「二维码失效时刻」与「本次调用起点 + 3 分钟」的**较早者** | 前者是 B 站定的时效；后者保证 `TimedOut` 后重入续等不会无限循环 |
| 同一 key 并发 | 用 `SemaphoreSlim` 串行化 | 同一 `qrcode_key` 并发轮询属于异常调用，且会造成重复登录事件 |

### 3.4 四个必须存在的出口

| 出口 | 触发 | 结果 |
| --- | --- | --- |
| ① 成功 | `data.code == 0`（或 HTTP 302） | `Succeeded` + 凭据已就绪 |
| ② 失效 | `data.code == 86038` | `Expired`，UI 需重新取码 |
| ③ 用户取消 | `CancellationToken` 被取消 | 抛 `OperationCanceledException`（**不吞**，由 UI 决定表现） |
| ④ 兜底超时 | 达到有效期上限 | `TimedOut`，即使一次 86038 都没收到也退出 |

第 ③ 条最容易漏：轮询若挂在不可取消的任务里，用户关掉登录界面后它仍会继续请求到有效期结束。

---

## 4. UI 开发预留的接口与契约

> 本节是"UI 该怎么做"的规格说明。服务端已按此实现并有测试覆盖；UI 只需遵守。

### 4.1 容器注册（已完成）

`DependencyInjection/ServiceCollectionExtensions.cs` 已在 `// Services` 段落注册：

```csharp
services.AddSingleton<ICredentialService, CredentialService>();
services.AddSingleton<IBiliLoginService, BiliLoginService>();
```

UI 侧直接构造函数注入 `IBiliLoginService`（需要凭据时再注入 `ICredentialService`）即可，不需要自己 new。

- 必须是 **Singleton**：登录态与 `CookieContainer` 在服务实例里常驻，
  用 Transient/Scoped 会导致每次拿到新实例、Cookie 丢失。
- 三个构造函数（无参 / `ILogger<T> + ICredentialService` / `HttpMessageHandler + ILogger<T> + ICredentialService`）中，
  容器会选第一个能解析全部参数的那条，无需额外配置。

### 4.2 主流程（ViewModel 侧的最简写法）

```csharp
private readonly IBiliLoginService _login;

// 1) 打开登录界面：取码
var session = await _login.CreateQrSessionAsync(token);
QrContent = session.Content;              // ← 交给二维码控件渲染

// 2) 订阅事件（界面打开时订阅，关闭时退订）
_login.StateChanged += OnLoginStateChanged;   // 状态 + 提示文案
_login.LoginSucceeded += OnLoginSucceeded;    // 登录后动作（加载头像、进主界面）

// 3) 开始轮询（把 UI 的取消令牌接进来！）
try
{
    var result = await _login.WaitForConfirmationAsync(session, progress, token);
    // Succeeded / Expired / TimedOut / NetworkFailed 四种终局分别提示
}
catch (OperationCanceledException) { /* 用户关闭了界面，静默收尾 */ }
```

**关键约束**：登录界面关闭时必须 `Cancel()` 那个令牌，否则轮询会继续到有效期结束。

### 4.3 二维码渲染契约（**服务不做**）

| 项 | 约定 |
| --- | --- |
| 输入 | `QrSession.Content`，形如 `https://www.bilibili.com/h5/login?qrcode_key=...` |
| 输出 | UI 用二维码库生成图片/位图控件 |
| 有效期提示 | 用 `QrSession.ExpiresAt`（`DateTimeOffset`）画倒计时或进度条 |
| 轮询凭据 | `QrSession.Key` **不需要** UI 接触，服务已自行保管 |
| 为什么不由服务渲染 | 服务层不引入图像与 UI 依赖，保持可测、跨平台（Android/iOS/Browser 各有自己的位图类型） |

UI 侧需要自行添加二维码库依赖（例如 ZXing.Net、QRCoder 等），这属于 UI 任务范围。

### 4.4 状态到界面的映射建议

| `LoginState` | 建议表现 | `Message`（服务已给中文文案） |
| --- | --- | --- |
| `WaitingForScan` | 显示二维码 + 提示扫码 | 请使用哔哩哔哩手机客户端扫描二维码 |
| `WaitingForConfirm` | 二维码灰化/打勾 + 提示确认 | 扫码成功，请在手机上确认登录 |
| `LoggedIn` | 关闭登录界面，展示头像昵称 | 登录成功 |
| `Expired` | 覆盖"二维码已失效"+ 刷新按钮 | 二维码已失效，请重新获取 |
| `TimedOut` | 同上（可提示超时） | 二维码已超时，请重新获取二维码 |
| `Failed` | 提示网络异常 + 重试按钮 | 网络异常，登录已中断，请稍后重试 |
| `Idle` | 未登录（登出后） | 已退出登录 |

### 4.5 头像与用户信息契约

| 调用 | 返回 | UI 侧注意 |
| --- | --- | --- |
| `CurrentProfile`（属性） | `BiliUserProfile?` | 登录后同步可读；可能为 `null`（资料接口失败） |
| `GetProfileAsync(token)` | `BiliUserProfile?` | `null` 表示未登录/凭据失效（服务已同步把状态置 `Expired`） |
| `GetAvatarImageAsync(uri, token)` | `byte[]?` | **服务不返回图片控件**：把字节交给 `new Bitmap(new MemoryStream(bytes))` 之类自行解码；`null` 时用占位图 |

`BiliUserProfile` 字段：`Mid`、`Nickname`、`AvatarUri`（已统一为 `https`）、`IsVip`、`Level`。

### 4.6 线程模型契约

- 所有异步方法可从任意线程调用。
- **事件在触发它的线程上同步回调**。流程由 UI 发起的 `await` 驱动，因此事件通常到达 UI 线程；
  但契约不保证这一点——UI 处理器内请自行做一次线程切换（如 `Dispatcher.UIThread.Invoke`）。
- `IProgress<T>` 建议用 `Progress<T>`（会自动回到创建它的同步上下文）。

### 4.7 凭据持久化契约（**由 `ICredentialService` 承担**）

`BiliLoginCredentials.SessData` **等价于账号登录态**，配合 `BiliJct` 可执行写操作。

| 责任 | 归属 |
| --- | --- |
| 内存中持有凭据 | `ICredentialService.Current`（**唯一读取入口**，登录服务不再暴露凭据） |
| 跨启动明文持久化 | `CredentialService`：`<LocalApplicationData>/BiliMusicDotNet/LoginCredential/credentials.json` |
| 恢复登录态 | `TryRestoreSessionAsync(credentials, token)`（内部会调 `nav` 校验） |
| 登出 / 失效时删除持久化副本 | 登录服务调用 `ICredentialService.ClearAsync()`，UI 不需要自己删 |
| 日志 | 只记录路径、Cookie **个数**与 `mid`，绝不输出凭据值 |

`BiliLoginCredentials.CookieHeader` 可直接用于为其它 HTTP 客户端附加登录态；
**只在与 B 站域名通信时附加**，禁止拼进 URL。

细节（目录、字段、失败边界、最小用法）见
[登录凭据服务文档](../Credential/LoginCredentialService文档.md)。

### 4.8 登出契约

`Logout()` 只做本地清理：清除 Cookie 容器中的凭据、请凭据服务清空内存并**删除磁盘副本**、
清空 `CurrentProfile`、状态回 `Idle`、触发 `LoggedOut`。
**不调用** B 站登出接口，不影响其它设备。

磁盘副本删除失败时抛 `StorageException`：此时 Cookie 与内存态已经清干净（本地登录确实退出了），
UI 需捕获该异常并提示用户本地可能仍留有凭据文件。

### 4.9 异常契约

| 异常 | 何时 | UI 建议 |
| --- | --- | --- |
| `BiliApiException` | 业务码非 0（如 `-412` 风控、响应缺字段、返回非 JSON） | 展示 `Message`，提供重试 |
| `HttpRequestException` | 网络层失败（DNS/TLS/断网） | 提示网络异常 |
| `OperationCanceledException` | 调用方取消 | 静默收尾（用户主动关闭界面） |
| `StorageException` | `Logout()` 删除磁盘副本失败 | 提示"已退出，但本地凭据文件删除失败" |
| `ObjectDisposedException` | 服务已释放 | 编程错误，不应出现在正常流程 |

`WaitForConfirmationAsync` **不抛**前两种：它把网络失败收敛为 `NetworkFailed` 终局，
只有"取消"会抛出（取消是控制流，不是错误）。
登录链路上的落盘失败同样不抛：只记 `LogError`，不把已经成立的登录判成失败。

---

## 5. 接口文档（API 参考）

> 本节是 Login 模块的**接口参考**：类型清单、成员签名、返回、抛错与副作用。
> "UI 该照着怎么写"见 §4。

### 5.1 类型清单

| 命名空间 | 类型 | 文件 |
| --- | --- | --- |
| `BiliMusicDotNet.Abstracts` | `IBiliLoginService`（契约） | `Abstracts/IBiliLoginService.cs` |
| `BiliMusicDotNet.Services` | `BiliLoginService`（`sealed`，默认实现） | `Services/BiliLoginService.cs` |
| `BiliMusicDotNet.Models.Login` | `QrSession` | `Models/Login/QrSession.cs` |
| `BiliMusicDotNet.Models.Login` | `QrStatusResult` | `Models/Login/QrStatusResult.cs` |
| `BiliMusicDotNet.Models.Login` | `QrLoginResult` | `Models/Login/QrLoginResult.cs` |
| `BiliMusicDotNet.Models.Login` | `QrLoginOutcome`（`enum`） | `Models/Login/QrLoginOutcome.cs` |
| `BiliMusicDotNet.Models.Login` | `LoginState`（`enum`） | `Models/Login/LoginState.cs` |
| `BiliMusicDotNet.Models.Login` | `BiliLoginCredentials` | `Models/Login/BiliLoginCredentials.cs` |
| `BiliMusicDotNet.Models.Login` | `BiliUserProfile` | `Models/Login/BiliUserProfile.cs` |
| `BiliMusicDotNet.Models.Login` | `LoginStateChangedEventArgs` | `Models/Login/LoginStateChangedEventArgs.cs` |
| `BiliMusicDotNet.Models.Login` | `LoginSucceededEventArgs` | `Models/Login/LoginSucceededEventArgs.cs` |
| `BiliMusicDotNet.Models.Login` | `LoggedOutEventArgs` | `Models/Login/LoggedOutEventArgs.cs` |
| `BiliMusicDotNet.Models.Exceptions` | `BiliApiException` | `Models/Exceptions/BiliApiException.cs` |
| `BiliMusicDotNet.Models.Exceptions` | `StorageException` | `Models/Exceptions/StorageException.cs` |
| `BiliMusicDotNet.Abstracts` | `ICredentialService`（契约） | `Abstracts/ICredentialService.cs` |
| `BiliMusicDotNet.Services` | `CredentialService`（`sealed`，默认实现） | `Services/CredentialService.cs` |

UI 层只需 `Abstracts` + `Models.Login` 两个命名空间即可编译，不必引用 `Services`（实现类只出现在组合根）。

### 5.2 构造、注册与释放

| 构造函数 | 用途 |
| --- | --- |
| `BiliLoginService(HttpMessageHandler handler, ILogger<BiliLoginService> logger, ICredentialService credentialService)` | 完整注入；单元测试与真机探针走这条（可换假传输、假存储或代理） |
| `BiliLoginService(ILogger<BiliLoginService> logger, ICredentialService credentialService)` | 容器默认选中的一条 |
| `BiliLoginService()` | XAML 设计器 / 无容器场景，日志落到 `NullLogger`，凭据服务走默认路径 |

- **必须注册为 Singleton**：`services.AddSingleton<IBiliLoginService, BiliLoginService>();`。登录态与 `CookieContainer` 都常驻在实例里，Transient/Scoped 会让每次解析都拿到空会话。
- 凭据服务同样注册为 Singleton，且必须注册：`services.AddSingleton<ICredentialService, CredentialService>();`（见 §4.7）。
- `Dispose()` 幂等：释放内部 `HttpClient` 与轮询信号量；释放后再调用任何成员抛 `ObjectDisposedException`。
- 不释放也不会泄漏：服务不持有计时器、后台线程或非托管句柄。

### 5.3 属性

| 成员 | 类型 | 语义 |
| --- | --- | --- |
| `State` | `LoginState` | 最近一次已知状态，由服务单方推进，UI 只读 |
| `CurrentProfile` | `BiliUserProfile?` | 昵称与头像地址；资料接口失败时为 `null`（**登录仍然成立**） |
| `IsLoggedIn` | `bool` | 等价于 `ICredentialService.Current is not null` |

凭据不再由本服务暴露：需要凭据（例如给其它 HTTP 客户端附加登录态）时读 `ICredentialService.Current`。

### 5.4 事件

| 事件 | 参数类型 | 触发时机 |
| --- | --- | --- |
| `StateChanged` | `LoginStateChangedEventArgs` | 每次状态推进：取码、扫码、待确认、成功、失效、超时、失败、登出 |
| `LoginSucceeded` | `LoginSucceededEventArgs` | 凭据到手**且已尝试抓取资料**；`Credentials` 必非空，`Profile` 可能为空 |
| `LoggedOut` | `LoggedOutEventArgs` | `Logout()` 完成后 |

三个事件都在**触发它的那个线程上同步回调**，不会自动切到 UI 线程（见 §4.6）。

### 5.5 方法

| 签名 | 返回 | 抛错 | 副作用 |
| --- | --- | --- | --- |
| `CreateQrSessionAsync(CancellationToken)` | `Task<QrSession>` | `BiliApiException`、`HttpRequestException`、`TaskCanceledException` | `State → WaitingForScan`，触发 `StateChanged` |
| `PollQrStatusAsync(QrSession, CancellationToken)` | `Task<QrStatusResult>` | `ArgumentNullException`、`HttpRequestException`、`TaskCanceledException` | 单次查询；命中成功时写入 Cookie、发布凭据、`State → LoggedIn` |
| `WaitForConfirmationAsync(QrSession, IProgress<QrStatusResult>?, CancellationToken)` | `Task<QrLoginResult>` | `ArgumentNullException`、`OperationCanceledException` | 轮询推进状态；成功时落盘凭据、抓取资料并触发 `LoginSucceeded` |
| `GetProfileAsync(CancellationToken)` | `Task<BiliUserProfile?>` | `HttpRequestException`、`TaskCanceledException` | 成功写 `CurrentProfile`；`nav` 返回 `-101` 时清凭据与磁盘副本并 `State → Expired` |
| `GetAvatarImageAsync(Uri?, CancellationToken)` | `Task<byte[]?>` | **不抛**（失败一律返回 `null`） | 无 |
| `TryRestoreSessionAsync(BiliLoginCredentials, CancellationToken)` | `Task<bool>` | `ArgumentNullException`、`StorageException` | 写 Cookie 容器，并让凭据服务持有与落盘；成功 `State → LoggedIn` 并触发 `LoginSucceeded`，失败清空登录态（含磁盘副本） |
| `Logout()` | `void` | `StorageException`（删除磁盘副本失败） | 清 Cookie、清内存与磁盘凭据、`State → Idle`、触发 `LoggedOut`；**不调用 B 站登出接口** |
| `static IsTerminal(LoginState)` | `bool` | — | 纯函数，判定 `LoggedIn` / `Expired` / `TimedOut` / `Failed` |

四条容易踩的边界：

- `PollQrStatusAsync` 是**单次查询**，命中成功时只组装凭据与状态，**不触发 `LoginSucceeded`** —— 该事件由 `WaitForConfirmationAsync` 在抓完资料后触发。
- `WaitForConfirmationAsync` **不抛** `BiliApiException`：连续网络失败收敛成 `NetworkFailed` 终局，只有取消会抛。
- `GetAvatarImageAsync` 连 `HttpRequestException` 都吞掉：头像下载失败不该拖垮已经成立的登录。
- 登录链路上的 **落盘失败也不抛**：登录已经成立，只记 `LogError`；`Logout()` 与 `TryRestoreSessionAsync` 里的落盘/删除失败则照常抛出。

### 5.6 数据模型

`QrSession` —— 一次扫码会话：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Key` | `string` | 轮询凭据 `qrcode_key`，服务自行保管，UI 不需要接触 |
| `Content` | `string` | 二维码内容，交给 UI 的二维码库渲染 |
| `ExpiresAt` | `DateTimeOffset` | 本地推算的失效时刻（约 3 分钟），可用来画倒计时 |

`QrStatusResult` —— 单次状态查询结果：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `State` | `LoginState` | 归一化后的状态 |
| `RawStatus` | `int` | B 站原始 `data.code`（`86101` / `86090` / `0` / `86038`），未知码原样保留 |
| `Message` | `string` | 接口提示文本，已剥除 `&nbsp;` 等 HTML 残留，可直接展示 |
| `Credentials` | `BiliLoginCredentials?` | 仅本次查询即登录成功时非空 |

`QrLoginResult` —— 轮询终局：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Outcome` | `QrLoginOutcome` | 终局类型 |
| `Credentials` | `BiliLoginCredentials?` | 仅 `Succeeded` 时非空 |
| `Profile` | `BiliUserProfile?` | 仅 `Succeeded` 时可能有；资料接口失败则为 `null` |
| `Message` | `string` | 面向用户的说明文本 |

`BiliLoginCredentials` —— 登录凭据：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `SessData` | `string` | 等价于账号登录态，持久化为明文、**禁止写日志** |
| `BiliJct` | `string` | CSRF 令牌，写操作（点赞 / 收藏 / 投币）需作为 `csrf` 参数提交 |
| `DedeUserId` | `string` | 当前用户 `mid` |
| `CookieHeader` | `string` | B 站下发的全部 `Set-Cookie` 拼成的 Cookie 头，可直接附加给其它 HTTP 客户端 |
| `IsComplete` | `bool`（只读计算属性） | 三个关键 Cookie 是否齐备 |

`BiliUserProfile` —— 用户展示信息：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Mid` | `long` | 用户 ID |
| `Nickname` | `string` | 昵称 |
| `AvatarUri` | `Uri?` | 头像地址，已统一升为 `https` |
| `IsVip` | `bool` | 是否大会员（`vipStatus == 1`） |
| `Level` | `int` | 用户等级，资料缺失时为 `0` |

事件参数与异常：

| 类型 | 成员 |
| --- | --- |
| `LoginStateChangedEventArgs` | `State`、`Message`、`Profile` |
| `LoginSucceededEventArgs` | `Credentials`（非空）、`Profile`（可空） |
| `LoggedOutEventArgs` | 无成员 |
| `BiliApiException` | `Code`（`int`）、`RequestPath`（`string`）；`Message` 形如 `Bilibili API [-412] ...（/x/...）` |

以上模型全部是 `sealed record` / `sealed class`，不可变，`record` 自带值相等语义。

### 5.7 枚举取值

`LoginState`：

| 取值 | 含义 | 是否终态 |
| --- | --- | --- |
| `Idle` | 未登录且无进行中的扫码流程 | 否 |
| `WaitingForScan` | 已取到二维码，等待扫码 | 否 |
| `WaitingForConfirm` | 已扫码，等待手机端确认 | 否 |
| `LoggedIn` | 登录成功，凭据已就绪 | ✅ |
| `Expired` | 二维码失效，需重新取码 | ✅ |
| `TimedOut` | 等待超时，需重新取码 | ✅ |
| `Failed` | 网络连续失败导致流程中断 | ✅ |

`QrLoginOutcome`：

| 取值 | 含义 |
| --- | --- |
| `Succeeded` | 登录成功，`Credentials` 非空 |
| `Expired` | 二维码已失效，必须重新取码 |
| `TimedOut` | 超过二维码有效期，本地兜底退出 |
| `NetworkFailed` | 连续网络失败达到上限；会话可能仍有效，可提示用户后重试续等 |

---

## 6. 文件清单

| 文件 | 作用 |
| --- | --- |
| `BiliMusicDotNet/Abstracts/IBiliLoginService.cs` | 契约：方法、事件、属性与 `IsTerminal` 终态判定（不含数据模型） |
| `BiliMusicDotNet/Abstracts/ICredentialService.cs` | 凭据契约：`Current` + `LoadAsync` / `SaveAsync` / `ClearAsync` |
| `BiliMusicDotNet/Models/Login/*.cs` | 数据模型（一个类型一个文件）：`QrSession`、`QrStatusResult`、`QrLoginResult`、`QrLoginOutcome`、`LoginState`、`BiliLoginCredentials`、`BiliUserProfile`、三个事件参数 |
| `BiliMusicDotNet/Models/Exceptions/*.cs` | 异常模型：`BiliApiException`（接口业务码）、`StorageException`（凭据读写失败） |
| `BiliMusicDotNet/Services/BiliLoginService.cs` | 实现：取码、轮询、凭据组装、用户信息、头像、恢复/登出 |
| `BiliMusicDotNet/Services/CredentialService.cs` | 实现：凭据的持有、明文落盘与清理 |
| `BiliMusicDotNet.Tests/QrSessionTests.cs` | 取码测试 |
| `BiliMusicDotNet.Tests/QrPollingTests.cs` | 轮询与终止出口测试 |
| `BiliMusicDotNet.Tests/UserProfileTests.cs` | 用户信息抓取测试 |
| `BiliMusicDotNet.Tests/AvatarTests.cs` | 头像下载测试 |
| `BiliMusicDotNet.Tests/SessionLifecycleTests.cs` | 恢复、登出、释放测试 |
| `BiliMusicDotNet.Tests/CredentialStorageTests.cs` | 凭据服务自身的读写与失败边界 |
| `BiliMusicDotNet.Tests/CredentialPersistenceTests.cs` | 登录 / 登出 / 失效与持久化的联动 |
| `BiliMusicDotNet.Tests/Support/FakeBiliHandler.cs` | 假 HTTP 传输与预置响应 |
| `BiliMusicDotNet.Tests/Support/FakeCredentialStore.cs` | 内存凭据替身（可配置落盘失败） |
| `BiliMusicDotNet.Tests/Support/TempDirectory.cs` | 测试用临时应用数据根目录 |
| `BiliMusicDotNet.Tests/README.md` | 测试项目入口说明（提交闸门、离线还原） |
| `tools/LoginQrProbe/Program.cs` | 探针入口与命令行解析 |
| `tools/LoginQrProbe/ProbeRunner.cs` | 真机链路编排与断言 |
| `tools/LoginQrProbe/ProbeResult.cs` | 断言集合与脱敏报告 |
| `tools/LoginQrProbe/OfflineSelfCheck.cs` | 离线自检（用假传输验证探针自身） |
| `tools/LoginQrProbe/QrConsoleRenderer.cs` | 二维码生成与终端渲染（QRCoder） |
| `tools/LoginQrProbe/README.md` | 探针用法、选项、断言清单 |
| `BiliMusicDotNet/DependencyInjection/ServiceCollectionExtensions.cs` | 服务注册（`ICredentialService` 与 `IBiliLoginService`，见 §4.1） |
| `docs/Services/Credential/LoginCredentialService文档.md` | 凭据服务文档（存储设计、契约、最小用法） |
