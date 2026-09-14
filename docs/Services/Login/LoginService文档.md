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
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` | 由各平台组合根注册；见 §5.1 |
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

分支：`feat/用户登录及头像显示`。

### 2.2 子功能与测试对照表

测试入口（**提交闸门**）：

```powershell
dotnet run --project .\BiliMusicDotNet.Tests\BiliMusicDotNet.Tests.csproj
# 判定依据：Total: 32, Errors: 0, Failed: 0
```

| # | 子功能 | 实现位置 | 测试 | 用例数 |
| --- | --- | --- | --- | --- |
| 1 | 取码（申请二维码会话） | `CreateQrSessionAsync` | `QrSessionTests` | 7 |
| 2 | 单次状态查询 | `PollQrStatusAsync` | `QrPollingTests.状态查询_*` | 4 |
| 3 | 轮询至终局 | `WaitForConfirmationAsync` | `QrPollingTests.终止_*` / `进度回调_*` | 6 |
| 4 | 凭据落地 | `PublishCredentials` / `BuildCredentials` | `QrPollingTests` 成功用例 | 含在 3 内 |
| 5 | 用户信息抓取 | `GetProfileAsync` | `UserProfileTests` | 5 |
| 6 | 头像下载 | `GetAvatarImageAsync` | `AvatarTests` | 4 |
| 7 | 登录态恢复 | `TryRestoreSessionAsync` | `SessionLifecycleTests.恢复登录态_*` | 3 |
| 8 | 登出 | `Logout` | `SessionLifecycleTests.登出_*` | 1 |
| 9 | 资源释放 | `Dispose` | `SessionLifecycleTests.释放_*` | 1 |
| 10 | 状态通知 | `StateChanged` / `LoginSucceeded` / `LoggedOut` | 各测试文件均有覆盖 | — |

### 2.3 验证现状（诚实声明）

| 项 | 状态 |
| --- | --- |
| 单元/集成测试（假 HTTP 传输，覆盖全部状态机分支与异常路径） | ✅ 32/32 通过 |
| 真机扫码验证工具 | ✅ 已就绪：`tools/LoginQrProbe`（见 §2.4） |
| 与真实 B 站接口的联调（真机扫码） | ⬜ **待执行**：工具已备好，需在能访问 `passport.bilibili.com` 的网络里跑一次 |
| 安卓 / iOS / 浏览器平台的构建 | ❌ 未验证（本次只改核心库的两个文件） |

开发本功能的机器无法访问 `passport.bilibili.com`（TLS 被拦截），
所以"接口形态与文档假设一致"这条只能靠探针在联网环境确认。**接入 UI 前请先跑通 §2.4。**

### 2.4 真机扫码验证探针

```powershell
dotnet run --project tools/LoginQrProbe                 # 真机扫码，终端渲染二维码
dotnet run --project tools/LoginQrProbe -- --proxy http://127.0.0.1:7897
dotnet run --project tools/LoginQrProbe -- --offline    # 离线自检，不联网
```

探针走完整链路并逐步断言，退出码 0 表示全部通过。它重点验证三件单元测试无法确认的事：

| 断言 | 若失败，说明要去核对本文哪一节 |
| --- | --- |
| 二维码内容是合法 bilibili https 登录地址 | §3.1 取码 |
| `nav` 返回昵称 | §5.3 Cookie 域 |
| 头像下载到非空字节 | §5.4 CDN Referer |

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
services.AddSingleton<IBiliLoginService, BiliLoginService>();
```

UI 侧直接构造函数注入 `IBiliLoginService` 即可，不需要自己 new。

- 必须是 **Singleton**：登录态与 `CookieContainer` 在服务实例里常驻，
  用 Transient/Scoped 会导致每次拿到新实例、Cookie 丢失。
- 三个构造函数（无参 / `ILogger<T>` / `HttpMessageHandler + ILogger<T>`）中，
  容器会选 `ILogger<BiliLoginService>` 那个，无需额外配置。

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

### 4.7 凭据持久化契约（**服务不做，UI/宿主必须做**）

`BiliLoginCredentials.SessData` **等价于账号登录态**，配合 `BiliJct` 可执行写操作。

| 责任 | 归属 |
| --- | --- |
| 内存中持有凭据 | 服务（`CurrentCredentials`） |
| 跨启动加密持久化 | **UI / 宿主**：Windows DPAPI、macOS Keychain、Linux Secret Service |
| 恢复登录态 | `TryRestoreSessionAsync(credentials, token)`（内部会调 `nav` 校验） |
| 登出时删除持久化副本 | UI / 宿主（`Logout()` 只清内存与 Cookie） |
| 日志 | 服务只记录 Cookie **个数**与 `mid`，绝不输出凭据值 |

`BiliLoginCredentials.CookieHeader` 可直接用于为其它 HTTP 客户端附加登录态；
**只在与 B 站域名通信时附加**，禁止拼进 URL。

### 4.8 登出契约

`Logout()` 只做本地清理：清除 Cookie 容器中的凭据、清空 `CurrentProfile`、状态回 `Idle`、触发 `LoggedOut`。
**不调用** B 站登出接口，不影响其它设备。若持久化过凭据，调用方需同步删除。

### 4.9 异常契约

| 异常 | 何时 | UI 建议 |
| --- | --- | --- |
| `IBiliLoginService.BiliApiException` | 业务码非 0（如 `-412` 风控、响应缺字段、返回非 JSON） | 展示 `Message`，提供重试 |
| `HttpRequestException` | 网络层失败（DNS/TLS/断网） | 提示网络异常 |
| `OperationCanceledException` | 调用方取消 | 静默收尾（用户主动关闭界面） |
| `ObjectDisposedException` | 服务已释放 | 编程错误，不应出现在正常流程 |

`WaitForConfirmationAsync` **不抛**上述前两种：它把网络失败收敛为 `NetworkFailed` 终局，
只有"取消"会抛出（取消是控制流，不是错误）。

---

## 5. 踩过的坑

### 5.1 登录成功有两种表现形态 ⚠️ 最容易漏

`poll` 成功时 B 站**可能返回 302**（`Location` + `Set-Cookie`，**响应体为空**），
而不是 `200` + 内层 `code: 0`。两种都要处理：

- `HttpClientHandler.AllowAutoRedirect = false`，否则 302 被自动跟随，`Set-Cookie` 拿不到。
- **302 路径不能去解析响应体**——初版实现在这里有 bug：解析空 body 直接抛"响应不是合法 JSON"，
  后来被测试抓到（现由"302 路径解析出全部凭据"用例守住）。
- 判定顺序：先看 HTTP 状态码是否 3xx，是则只从 `Set-Cookie` 判定成功；否则再解析 JSON 读内层 `code`。

### 5.2 轮询状态读内层 `data.code`，不是外层 `code`

外层 `code` 只表示"接口调用成功"（正常恒为 0），真正的扫码状态在内层 `data.code`。
只读外层会**永远停在"未扫码"**，直到兜底超时——表现为"扫码了但一直不登录"。
`QrPollingTests.状态查询_读内层状态码而不是外层` 专门守这条。

### 5.3 Cookie 的域要用 `bilibili.com`，不要用 `passport.bilibili.com`

登录成功后 Cookie 由 `passport.bilibili.com` 下发，但后续业务请求打在 `api.bilibili.com`。
若把 Cookie 的域记成 `passport.bilibili.com`，它**不会**发给 `api.bilibili.com`，
表现为"登录成功了，但取不到昵称头像（`nav` 返回 `-101`）"。

实测结论（`UserProfileTests.用户信息_凭据通过Cookie容器发往api域` 断言了这一点）：
`CookieContainer` 在 `Cookie(name, value, "/", "bilibili.com")` 时会按域后缀匹配，
`api.bilibili.com` 能收到；因此统一用 `bilibili.com` 即可，无需手工拼 `Cookie` 头。

### 5.4 两个域的 `Referer` 不能混用

- `passport.bilibili.com` 的取码/轮询：`Referer: https://passport.bilibili.com/login`
- `api.bilibili.com` 与 `hdslb.com` CDN：`Referer: https://www.bilibili.com`

头像 CDN 缺 `Referer` 会直接 **403**。服务内部按接口写死，UI 不需要关心。

### 5.5 `nav` 是特例：未登录时返回 `code: -101`

它既是登录态查询接口，也是 WBI 密钥来源。因此**不能**套用"`code != 0` 即抛错"的统一封装。
本服务对它的处理是：`-101` → 判定登录态失效 → 清空凭据 + 状态置 `Expired` + 返回 `null`。

### 5.6 `DedeUserID__ckMd5` 这类值含 `=`，`Set-Cookie` 切分要小心

一个 302 响应会带 **4 个** `Set-Cookie` 头。切分时必须：

1. 按 `;` 取第一段（丢掉 `Path` / `Domain` / `HttpOnly`）；
2. 只按**第一个** `=` 切 name/value（值里可能还有 `=`）。

用 `Split('=')` 全切会导致值被截断。

### 5.7 登录成功也可能"其实没登录"

拿到 Cookie 后立刻调 `nav` 校验：若返回 `-101`，说明凭据不可用（异常情况，例如被风控）。
此时**不能对外宣称登录成功**——初版实现在这里会返回 `Succeeded` 却带着失效凭据，
测试 `UserProfileTests.用户信息_凭据到手但nav判定未登录时不谎报成功` 抓到了它，现在返回 `Expired`。

### 5.8 日志里绝不能出现 `SESSDATA`

它等价于账号登录态。服务的日志只出现：Cookie **个数**、`mid`、状态、原始状态码。
排查时如确需定位，只打印长度或哈希前缀。

### 5.9 工具链坑（环境相关，非业务逻辑）

| 坑 | 现象 | 处理 |
| --- | --- | --- |
| Avalonia 构建遥测写用户目录 | `AvaloniaStatsTask` 抛 `UnauthorizedAccessException: ...\AppData\Local\AvaloniaUI\BuildServices\buildtasks.log`，**构建直接失败** | 构建时传 `-p:UsedAvaloniaProducts=`（或同类开关）跳过遥测任务 |
| VSTest `testhost` 子进程 | `Testhost 进程已退出，但出现错误: Win32Exception (5) 拒绝访问`（`Process.GetProcessHandle`） | 测试项目改用 xunit.v3 + Microsoft.Testing.Platform，`dotnet run` 直接在测试程序集内执行 |
| `dotnet test` 在 SDK 10 上强制 VSTest | `Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later` | 用 `dotnet run --project ...Tests` 作为测试入口；详见测试项目 README |
| nuget.org 不可达 | `NU1900`、还原失败、无法添加新包依赖 | 只用本机 NuGet 缓存中已有的包版本；测试项目自带离线源脚本 |
| `dotnet build` 报 NU1900 警告 | 漏洞数据库拉取失败（离线） | 无害；不影响构建结果 |

### 5.10 契约类型内嵌在接口里的取舍

为把改动限制在 `IBiliLoginService.cs` / `BiliLoginService.cs` 两个文件内，
所有契约类型（`QrSession`、`LoginState`、`BiliLoginCredentials`、事件参数、异常）都**内嵌在接口里**：

- 好处：文件数少、类型天然带命名空间前缀（`IBiliLoginService.QrSession`），不会污染 `Abstracts` 命名空间。
- 代价：C# **不会**把嵌套类型继承给实现类，所以 `BiliLoginService.cs` 顶部需要一组
  `using X = IBiliLoginService.X;` 别名；调用方写类型名也偏长。
- 后续如果 UI 侧觉得类型名太长，可在不改行为的前提下把它们提升为 `Abstracts` 命名空间下的顶层类型
  （属于纯重构，测试可作回归保护）。

### 5.11 按子功能分阶段提交的经验

本次实现按"取码 → 轮询 → 用户信息 → 头像 → 生命周期"分阶段提交，每个阶段都是一次可编译、可测的增量。
这样做的额外收益是：接口文件与测试文件天然按子功能切分，review 时能逐段对照。
代价是要维护"阶段内不能引用后续阶段的类型"这条纪律（例如轮询阶段不能返回 Profile）。

### 5.12 轮询节奏与"登录后再预热"

登录成功后**不要**立刻批量拉榜单/历史/关注做"预热"，也不要在轮询期间叠加其它内容接口请求——
`-412` 风控主要发生在登录后的内容接口调用模式上。本服务只调登录链路所需的 3 个接口。

### 5.13 二维码渲染不要自己写，用 QRCoder

真机验证探针需要把二维码画到终端。一开始为了"零依赖"自写了一个极简编码器，结果是花在调试它上面的时间
远超它省下的那点依赖 —— 二维码有太多"错了也不报错、只是扫不出来"的细节：

| 自写时踩到的坑 | 后果 |
| --- | --- |
| 格式信息 BCH 写成"左移一位判第 9 位"的变体 | 15 位串不匹配任何合法码字，图看着正常但扫不动 |
| 版本 10 起的字符计数指示符是 16 位（1~9 是 8 位），按 8 位算 | 长内容被截断 |
| 把版本 7 才存在的版本信息区当成保留区（低版本那 36 个模块是数据模块） | 数据位整体错位 24 个模块 |
| 容量估算只减模式指示符、忘了字符计数字段 | 边界长度选错版本 |

判据留档（L 级 8 个掩码的格式串，可用于校验任何实现）：
`0x77C4, 0x72F3, 0x7DAA, 0x789D, 0x662F, 0x6318, 0x6C41, 0x6976`。

**结论**：改用 [QRCoder](https://github.com/Shane32/QRCoder)（`AsciiQRCode` 渲染器，不依赖 System.Drawing）。
这类"标准明确、细节繁多"的编解码，用成熟库远比自写划算；自写唯一的收益是少一个依赖，代价却是几轮难查的调试。

---

## 6. 文件清单

| 文件 | 作用 |
| --- | --- |
| `BiliMusicDotNet/Abstracts/IBiliLoginService.cs` | 契约：方法、状态枚举、内嵌返回类型、事件参数、`BiliApiException` |
| `BiliMusicDotNet/Services/BiliLoginService.cs` | 实现：取码、轮询、凭据解析、用户信息、头像、恢复/登出 |
| `BiliMusicDotNet.Tests/QrSessionTests.cs` | 取码测试 |
| `BiliMusicDotNet.Tests/QrPollingTests.cs` | 轮询与终止出口测试 |
| `BiliMusicDotNet.Tests/UserProfileTests.cs` | 用户信息抓取测试 |
| `BiliMusicDotNet.Tests/AvatarTests.cs` | 头像下载测试 |
| `BiliMusicDotNet.Tests/SessionLifecycleTests.cs` | 恢复、登出、释放测试 |
| `BiliMusicDotNet.Tests/Support/FakeBiliHandler.cs` | 假 HTTP 传输与预置响应 |
| `BiliMusicDotNet.Tests/README.md` | 测试项目入口说明（提交闸门、离线还原） |
| `tools/LoginQrProbe/Program.cs` | 探针入口与命令行解析 |
| `tools/LoginQrProbe/ProbeRunner.cs` | 真机链路编排与断言 |
| `tools/LoginQrProbe/ProbeResult.cs` | 断言集合与脱敏报告 |
| `tools/LoginQrProbe/OfflineSelfCheck.cs` | 离线自检（用假传输验证探针自身） |
| `tools/LoginQrProbe/QrConsoleRenderer.cs` | 二维码生成与终端渲染（QRCoder） |
| `tools/LoginQrProbe/README.md` | 探针用法、选项、断言清单 |
| `BiliMusicDotNet/DependencyInjection/ServiceCollectionExtensions.cs` | 服务注册（`AddSingleton<IBiliLoginService, BiliLoginService>()`，见 §4.1） |
