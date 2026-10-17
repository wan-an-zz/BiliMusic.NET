# 登录凭据服务（CredentialService）维护文档

> 本文档对应"登录凭据持久化"的落地过程：存储约定、契约、验证方式与最小用法。
> **本服务不含任何 UI 代码**，也不负责抓取昵称头像——展示信息仍由
> [登录服务](../Login/LoginService文档.md) 的 `CurrentProfile` 提供。
>
> 相关文档：[登录服务文档](../Login/LoginService文档.md)（§4.7 凭据契约的由来）、
> [凭据加密服务文档](EncryptService文档.md)、
> [Bilibili API 参考](../../bili_api/bilibili-api-reference.md)

---

## 1. 技术栈

| 关注点 | 选型                                                                    | 说明 |
| --- |-------------------------------------------------------------------------| --- |
| 语言 / 框架 | .NET 10（`net10.0` / `net10.0-windows` / `net10.0-android`）、C# latest | 与主工程一致；文件级命名空间、`record`、主构造函数 |
| JSON | `System.Text.Json` + **源生成**（`JsonSerializerContext`）              | 裁剪与 AOT 友好；直接序列化 `BiliLoginCredentials`，不另建 DTO |
| 字段命名 | `JsonNamingPolicy.CamelCase`                                            | 落盘形如 `sessData`；Cookie 名仍留在 `CookieHeader` 的值里 |
| 加密 | `DPAPI` / `AndroidKeystore `，各平台 head 提供实现                      | 密钥由平台保管；桌面端 DPAPI、Android 端系统密钥库，见 [凭据加密服务文档](EncryptService文档.md) |
| 文件位置 | `Environment.SpecialFolder.LocalApplicationData`                        | 由 BCL 按平台解析，不写平台分支 |
| 原子写 | 先写 `credentials.dat.tmp`，再 `File.Move(..., overwrite: true)`        | 进程中断不会留下半个文件 |
| 日志 | `Microsoft.Extensions.Logging.ILogger<T>`（经 NLog 输出）               | 构造函数注入；只记路径、`mid`、失败原因 |
| 依赖注入 | `Microsoft.Extensions.DependencyInjection`                              | 由各平台组合根注册；见 §5.1 |
| 测试 | xUnit v3（`xunit.v3` 3.2.2）+ Microsoft.Testing.Platform                | 提交闸门见 §2.2 |

---

## 2. 子功能、提交与测试

### 2.1 提交对照表

| 提交 | 内容 |
| --- | --- |
| `08e21b6` `refactor(login): 异常模型集中到 Models/Exceptions` | `BiliApiException` 搬到 `Models/Exceptions/`，为新增 `StorageException` 腾出统一位置 |
| `153ebe6` `feat(credential): 新增凭据契约与内存持有者` | `StorageException`、`ICredentialService`、`CredentialService`、`CredentialStorageTests` |
| `553182c` `refactor(login): 凭据改由 CredentialService 持有` | 登录服务删除 `CurrentCredentials`、登录成功落盘、登出与失效清理、`CredentialPersistenceTests`、容器注册 |
| `ae24b2a` `refactor(login)!: 登录结果不再对外暴露凭据` | 登录结果去掉 `Credentials`：凭据一律经 `CredentialService` 读，探针与测试同步改口 |
| （待提交）`feat(credential): 凭据落盘改为平台加密` | `IEncryptService` 契约、桌面端 DPAPI、Android 端密钥库 AES-GCM、iOS 占位实现、文件改名为 `credentials.dat` 并清理旧明文文件 |

分支：`feature/Login`。

### 2.2 测试入口（提交闸门）

```powershell
dotnet run --project .\BiliMusicDotNet.Tests\BiliMusicDotNet.Tests.csproj
# 判定依据：Total: 112, Errors: 0, Failed: 0
```

> 当前有 9 个既有的 UI 弹窗用例为红（`AboutPage*Tests` / `QrLoginViewTests`），与本服务无关：
> 本次改动前的基线同样是这 9 个。

### 2.3 子功能与测试对照表

| # | 子功能 | 实现位置 | 测试 | 用例数 |
| --- | --- | --- | --- | --- |
| 1 | 凭据落盘（加密 + 原子写） | `CredentialService.SaveAsync` | `CredentialStorageTests` / `CredentialPersistenceTests.登录成功后*` | 3 |
| 2 | 凭据读回（写进 `Current`） | `CredentialService.LoadAsync` | `CredentialStorageTests.保存后*` / `读回的结果*` | 2 |
| 3 | 只读语义（不建目录） | `CredentialService.LoadAsync` | `CredentialStorageTests.文件不存在时*` | 1 |
| 4 | 损坏与不完整数据的兜底 | `CredentialService.LoadAsync` | `CredentialStorageTests.密文损坏时*` / `解密失败时*` / `凭据不完整时*` | 3 |
| 5 | 清理与幂等 | `CredentialService.ClearAsync` | `CredentialStorageTests.清理后*` / `CredentialPersistenceTests.登出后*` | 2 |
| 6 | 写盘失败的异常契约 | `CredentialService.SaveAsync` | `CredentialStorageTests.目录不可写时*` | 1 |
| 7 | 路径解析 | `CredentialService.ResolveFilePath` | `CredentialStorageTests.默认路径*` | 1 |
| 8 | 登录成功自动落盘 | `BiliLoginService.CompleteLoginAsync` | `CredentialPersistenceTests.登录成功后*` | 3 |
| 9 | 恢复登录态时读盘 | `BiliLoginService.TryRestoreSessionAsync` | `CredentialPersistenceTests.恢复登录态_从磁盘读回凭据并建立会话` | 1 |
| 10 | 失效时清理磁盘副本 | `BiliLoginService.GetProfileAsync` | `CredentialPersistenceTests.nav判定失效时*` | 1 |
| 11 | 落盘失败不阻断登录 | `BiliLoginService.SaveCredentialSafelyAsync` | `CredentialPersistenceTests.落盘失败不影响登录成立` | 1 |
| 12 | 落盘前加密、读盘后解密 | `CredentialService.SaveAsync` / `LoadAsync` | `CredentialStorageTests.交给加密服务的是凭据JSON明文` / `EncryptServiceTests.与凭据服务串起来*` | 2 |
| 13 | 加解密失败的边界 | `CredentialService` 的两处 `catch` | `CredentialStorageTests.解密失败时*` / `加密失败时*` | 2 |
| 14 | 平台加密实现本身 | `DpapiEncryptService` | `EncryptServiceTests` | 6 |

---

## 3. 存储设计

### 3.1 目录与文件

```
<LocalApplicationData>/
└── BiliMusicDotNet/
    ├── logs/                              ← NLog 输出（既有）
    └── LoginCredential/
        └── credentials.dat                ← 本服务唯一的文件（密文）
```

`<LocalApplicationData>` 由 `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` 解析：

| 平台 | 典型取值 |
| --- | --- |
| Windows | `C:\Users\<user>\AppData\Local` |
| macOS | `/Users/<user>/.local/share` |
| Linux | `/home/<user>/.local/share` |
| Android / iOS | 由平台映射到应用私有可写目录 |

全路径拼接固定为 `<baseDirectory>/BiliMusicDotNet/LoginCredential/credentials.dat`，
对应 `CredentialService.ResolveFilePath`——改动它等于让老用户的凭据失效。

同一个目录下的 `credentials.json` 是加密改造前的明文文件，服务既不读回也不删除（见 §3.5）。

### 3.2 文件内容

文件是**密文**，没有可读结构；下面是被加密之前的明文载荷：

```json
{
  "sessData": "……",
  "biliJct": "……",
  "dedeUserId": "123456",
  "cookieHeader": "SESSDATA=……; bili_jct=……; DedeUserID=123456; DedeUserID__ckMd5=……"
}
```

| 字段 | 来源 | 说明 |
| --- | --- | --- |
| `sessData` | `BiliLoginCredentials.SessData` | 等价于账号登录态；加密后落盘，禁止写日志 |
| `biliJct` | `BiliLoginCredentials.BiliJct` | CSRF 令牌，写操作需要 |
| `dedeUserId` | `BiliLoginCredentials.DedeUserId` | 用户 `mid` |
| `cookieHeader` | `BiliLoginCredentials.CookieHeader` | 完整 Cookie 头，可直接附加给其它 HTTP 客户端 |

明文载荷里**只有输入字段**：`IsComplete` 是派生属性，已用 `[JsonIgnore]` 排除，
因此"读回的结果"完全由文件内容决定，可复现。

### 3.3 写入原子性

1. `Directory.CreateDirectory`（服务自己保证目录存在，不依赖安装步骤）；
2. 序列化成 UTF-8 JSON；
3. 交给 `IEncryptService.Encrypt`，拿到密文（失败抛 `CryptographicException`）；
4. 密文写入 `credentials.dat.tmp`；
5. `File.Move(tmp, target, overwrite: true)` 覆盖替换；
6. 第 2~5 步任一步失败：尽力删除 `.tmp`，再抛 `StorageException`。

任何时刻磁盘上要么是旧文件、要么是新文件，不会是半个文件。

### 3.4 `Current` 与磁盘的关系

| 操作 | 返回值 | `Current` | 磁盘 |
| --- | --- | --- | --- |
| `LoadAsync`（读到） | `true` | 写入读回值 | 不变（只读） |
| `LoadAsync`（读不到） | `false` | **不变** | 不变 |
| `SaveAsync` | `Task` | 先更新 | 后写入；写失败时已经是新值 |
| `ClearAsync` | `Task` | 先清空 | 后删除；删失败时已经为空 |

**写盘失败时内存与磁盘会短暂不一致**：`Current` 是新凭据，文件还是旧的。
这是"写盘失败向上抛异常"的直接后果，调用方要么重试，要么在捕获异常后自行 `ClearAsync`。

### 3.5 旧明文文件

加密改造前磁盘上留的是明文 `credentials.json`。服务对它**既不读回也不删除**：

- 不读回：没有迁移逻辑，老用户升级后表现为"需要重新扫码"。读回明文再加密写回会让明文多活一轮，
  还要处理"写到一半失败时两份文件并存"，而重新扫码的代价只是扫一次。
- 不删除：`credentials.json` 会继续留在 `LoginCredential/` 目录里，**它是一份明文凭据**。
  升级过的机器需要手动删掉它。

同名目录下的 `credentials.dat` 与本文件无关，删掉 `credentials.json` 不影响已登录的凭据。

服务在**保存成功之后**与**清理时**顺手删掉它；删除失败只记 warning，
因为"这份文件已经没有任何代码会读"。

---

## 4. 业务逻辑流程

### 4.1 扫码登录（服务自动落盘）

```
UI                      BiliLoginService              CredentialService          磁盘
 │                             │                              │                    │
 │── 扫码登录（略）──────────▶ │                              │                    │
 │                             │ 拿到 Set-Cookie 组装凭据      │                    │
 │                             │── SaveAsync(credentials) ──▶ │── 序列化 → 加密 ──│
 │                             │                              │── 写 .tmp ───────▶ │
 │                             │                              │── Move 覆盖 ─────▶ │
 │                             │◀── 完成 / StorageException ──│                    │
 │◀── LoginSucceeded ──────────│（失败只记日志，登录仍成立）    │                    │
```

### 4.2 启动恢复（ViewModel 只驱动登录服务）

凭据的读盘发生在 `BiliLoginService.TryRestoreSessionAsync()` 内部：

```
ViewModel              BiliLoginService            CredentialService         磁盘
    │                        │                            │                    │
    │── TryRestoreSessionAsync() ──▶                     │                    │
    │                        │── LoadAsync() ──────────▶  │── 读文件 ────────▶ │
    │                        │                            │── 解密 → 反序列化 ─│
    │                        │◀─ false ──────────────────│（不存在/解不开/不完整）│
    │◀── false ──────────────│                            │                    │
    │   （显示登录界面，未发任何请求）                       │                    │
    │                        │                            │                    │
    │── TryRestoreSessionAsync() ──▶                     │                    │
    │                        │── LoadAsync() ──────────▶  │── 读文件 ────────▶ │
    │                        │◀─ true（Current 已就绪）───│                    │
    │                        │  把 Current 的 Cookie 装进容器                    │
    │                        │── nav 校验 ──────────────────────────────────▶ B 站
    │◀── true（已自动登录）───│                            │                    │
```

- 返回 `false` 有两条分支：**读不到凭据**（状态保持 `Idle`，不发生任何 HTTP 请求）与
  **凭据已失效**（`nav` 返回 `-101`，清空内存与磁盘副本，状态置 `Expired`）。
- ViewModel 只按返回值决定"进主界面"还是"显示登录界面"，凭据始终没有离开服务层。

### 4.3 登出与失效

| 触发 | 结果 |
| --- | --- |
| `Logout()` | 清 Cookie → 清内存 → **删除文件** → `State = Idle` → 触发 `LoggedOut` |
| `nav` 返回 `-101` | 判定登录态失效 → 清内存 → **删除文件** → `State = Expired` |

两处都保证"磁盘上不再留有等价于登录态的副本"；删除失败时的差别见 §6。

---

## 5. 契约与注册

### 5.1 容器注册（已完成）

共享注册（`DependencyInjection/ServiceCollectionExtensions.cs` 的 `// Services` 段落）：

```csharp
services.AddSingleton<ICredentialService, CredentialService>();
services.AddSingleton<IBiliLoginService, BiliLoginService>();
```

加密实现由各平台 head 注册，共享层不注册（它无法决定用哪个实现）：

| 平台 | 注册 | 实现 |
| --- | --- | --- |
| 桌面端 | `Program.ConfigureServices` | `AddSingleton<IEncryptService, DpapiEncryptService>()` |
| Android | `Application.BuildServices` | `AddSingleton<IEncryptService, AndroidKeystoreEncryptService>()` |

- 必须是 **Singleton**：`Current` 是进程内唯一状态，Transient / Scoped 会让每次解析都拿到空凭据。
- 注册顺序放在登录服务之前：登录服务依赖它。
- 各平台容器都开了 `ValidateOnBuild`：漏注册 `IEncryptService` 会在启动时立刻暴露，而不是等到登录。
- 需要登录态时读`IBiliLoginService.IsLoggedIn`，需要恢复时调 `TryRestoreSessionAsync()`。

### 5.2 构造

| 构造函数 | 用途 |
| --- | --- |
| `CredentialService(string? baseDirectory, IEncryptService encryptService, ILogger<CredentialService> logger)` | 完整注入；测试与探针走这条（`baseDirectory` 传临时目录） |
| `CredentialService(IEncryptService encryptService, ILogger<CredentialService> logger)` | 容器选中的一条，路径用操作系统约定的应用数据目录 |

加密改造前存在过无参构造（走默认路径 + `NullLogger`），现在已删除：加密服务是平台相关的，
共享层给不出一个安全的默认值。

---

## 6. 依赖与约束

1. **加密是唯一的保护手段**：`sessData` 等价于账号登录态，文件被解开即等于账号被接管。日志里只允许出现路径、`mid` 与字段是否齐备，**绝不允许出现凭据值**。
2. **密文绑定机器与用户**：DPAPI 用当前用户的密钥，Android 用设备密钥库。换机器、换系统用户、清空密钥库或卸载重装（Android 密钥随应用删除）都会让旧密文解不开——表现为"未登录"，需要重新扫码。
3. **单账号单文件**：只保存"当前账号"，换账号直接覆盖；不做多账号切换。
4. **没有 schema 版本头**：`BiliLoginCredentials` 增删字段后，旧文件里缺失的字段会反序列化成空值；`LoadAsync` 的 `IsComplete` 校验会把它拦成 `false`（视为未登录），不会把坏数据当成登录态。
5. **`LoadAsync` 不删脏文件**：启动路径只读不写，解不开或损坏的文件会一直留到下一次登录覆盖或登出清理。
6. **只有本服务保管登录凭据**：运行期凭据完全由 `CredentialService` 保管，需要知道"是否已登录"时读 `IBiliLoginService.IsLoggedIn`。
7. **测试必须注入假存储或临时目录**：真实路径落在开发机自己的 `%LOCALAPPDATA%`，直接 `new CredentialService(...)` 会让测试污染本机数据。测试侧统一用 `Support/TempDirectory` + `Support/FakeEncryptService`（真实实现）或 `Support/FakeCredentialStore`（内存替身）。
8. **登录链路会吞掉落盘异常**：登录已经成立（用户手机上确认过）时，磁盘写失败只记 `LogError`，不把登录判成失败。后果是"这次登录成功但下次启动不能自动登录"，排查时看日志里的 `登录已成立但凭据未能落盘`。
9. **`Logout()` 的删除异常向上抛**：`Logout()` 是同步 `void`，无法把失败藏在返回里。抛出时 Cookie 与内存态已经清干净，即**本地登录确实退出了**，但磁盘副本可能仍在——UI 必须捕获并告知用户（见 §8.2）。
10. **失效清理不抛异常**：`nav` 返回 `-101` 时的磁盘清理失败只记 warning——此时"登录态已失效"才是要传达给用户的结论。

---

## 7. API 参考

### 7.1 类型清单

| 命名空间 | 类型 | 文件 |
| --- | --- | --- |
| `BiliMusicDotNet.Abstracts` | `ICredentialService`（契约） | `Abstracts/ICredentialService.cs` |
| `BiliMusicDotNet.Abstracts` | `IEncryptService`（加密契约） | `Abstracts/IEncryptService.cs` |
| `BiliMusicDotNet.Services` | `CredentialService`（`sealed`，默认实现） | `Services/CredentialService.cs` |
| `BiliMusicDotNet.Models.Exceptions` | `StorageException` | `Models/Exceptions/StorageException.cs` |
| `BiliMusicDotNet.Models.Login` | `BiliLoginCredentials`（同时是落盘模型） | `Models/Login/BiliLoginCredentials.cs` |

### 7.2 成员

| 成员 | 返回 | 抛错 | 副作用 |
| --- | --- | --- | --- |
| `Current` | `BiliLoginCredentials?` | — | — |
| `LoadAsync(CancellationToken)` | `bool`；文件不存在 / 解密失败 / JSON 损坏 / 字段缺失 / `IsComplete == false` → `false` | 不抛 | 成功时写 `Current`；只读，不创建目录 |
| `SaveAsync(BiliLoginCredentials, CancellationToken)` | `Task` | `ArgumentNullException`；`StorageException` | 更新 `Current`、建目录、加密后原子覆盖写 |
| `ClearAsync(CancellationToken)` | `Task` | `StorageException` | 清 `Current`、删文件（不存在视为成功） |
| `static ResolveFilePath(string baseDirectory)` | `string` | — | 纯函数，路径拼接的唯一出处 |

调用顺序固定为"先 `LoadAsync`，再从 `Current` 取"：

```csharp
if (await credentials.LoadAsync(token))
{
    var saved = credentials.Current;   // 读盘成功时必非空
}
```

`StorageException` 成员：`Operation`（`"写入"` / `"删除"`）、`Path`（出错文件路径）、
`InnerException`（`CryptographicException` / `IOException` / `UnauthorizedAccessException` / `JsonException` / `NotSupportedException`）。

---

## 8. 最小使用示例

### 8.1 启动时恢复登录态（ViewModel 侧）

```csharp
private readonly IBiliLoginService _login;   // 不注入 ICredentialService

public async Task RestoreOnStartupAsync(CancellationToken token)
{
    // 读盘、解密、装 Cookie、nav 校验全在服务内部完成。
    if (await _login.TryRestoreSessionAsync(token))
    {
        // 已登录：昵称头像用 _login.CurrentProfile
        return;
    }

    // 返回 false：要么从来没有凭据，要么凭据已失效（失效时服务已清掉内存与磁盘副本）。
    ShowLoginPage();
}
```

### 8.2 登出（磁盘删除可能失败）

```csharp
try
{
    _login.Logout();              // 清 Cookie、清内存、删磁盘副本
}
catch (StorageException ex)
{
    // 本地登录已经退出，但磁盘上可能仍留着凭据文件。
    _logger.LogWarning(ex, "登出时未能删除本地凭据副本");
    NotifyUser("已退出登录，但本地凭据文件删除失败，建议手动清理。");
}

// 无论是否抛过异常，界面都按"已登出"渲染：
Nickname = null;
Avatar = null;
```

### 8.3 排障时人工确认落盘情况

文件是密文，`Get-Content` 看不出任何东西（这正是目的），只能确认"在不在、多大、是不是刚写的"：

```powershell
Get-Item "$env:LOCALAPPDATA\BiliMusicDotNet\LoginCredential\credentials.dat" |
    Select-Object Length, LastWriteTime
```

要判断"这份凭据还能不能用"，看启动日志里有没有
`已读回登录凭据：mid=……`；解不开时那里是 `凭据文件无法读取，按未登录处理`。

---

## 9. 非目标

- 用户档案（昵称 / 头像 / 等级）的持久化——本次不做，展示信息仍由登录服务的内存态提供；
- 多账号并存与切换；
- 存储 schema 版本头与迁移；
- 旧明文文件的内容迁移（既不读回也不删除，理由与后果见 §3.5）；
- 应用启动时自动恢复登录态（服务只提供能力，编排属于 UI 任务）；
- 登出时删除整个目录、清理历史残留；

---

## 10. 文件清单

| 文件 | 作用 |
| --- | --- |
| `BiliMusicDotNet/Abstracts/ICredentialService.cs` | 契约：`Current` + `LoadAsync` / `SaveAsync` / `ClearAsync` |
| `BiliMusicDotNet/Abstracts/IEncryptService.cs` | 加密契约：`Encrypt` / `Decrypt`（流进、`byte[]` 出） |
| `BiliMusicDotNet/Services/CredentialService.cs` | 实现：路径解析、加密、原子写、损坏兜底、源生成序列化上下文 |
| `BiliMusicDotNet/Models/Exceptions/StorageException.cs` | 写盘 / 删除失败的异常类型 |
| `BiliMusicDotNet/Models/Exceptions/BiliApiException.cs` | 接口业务码异常（本次从 `Models/Login/` 迁入） |
| `BiliMusicDotNet/Models/Login/BiliLoginCredentials.cs` | 凭据模型，同时是落盘模型（`IsComplete` 不落盘） |
| `BiliMusicDotNet/DependencyInjection/ServiceCollectionExtensions.cs` | 共享服务注册（§5.1） |
| `BiliMusicDotNet.Desktop/Services/DpapiEncryptService.cs` | 桌面端加密实现（DPAPI） |
| `BiliMusicDotNet.Android/Services/AndroidKeystoreEncryptService.cs` | Android 端加密实现（系统密钥库 + AES-GCM） |
| `BiliMusicDotNet.iOS/Services/UnsupportedEncryptService.cs` | iOS 端占位实现 |
| `BiliMusicDotNet.Tests/CredentialStorageTests.cs` | 凭据服务自身的读写、加密、清理与失败边界 |
| `BiliMusicDotNet.Tests/CredentialPersistenceTests.cs` | 登录 / 登出 / 失效与持久化的联动 |
| `BiliMusicDotNet.Tests/EncryptServiceTests.cs` | 桌面端加密实现的往返与失败边界 |
| `BiliMusicDotNet.Tests/Support/TempDirectory.cs` | 测试用临时应用数据根目录 |
| `BiliMusicDotNet.Tests/Support/FakeCredentialStore.cs` | 内存替身，可配置落盘失败 |
| `BiliMusicDotNet.Tests/Support/FakeEncryptService.cs` | 加解密替身（逐字节异或），供共享层测试与探针复用 |
| `tools/LoginQrProbe/ProbeRunner.cs`、`OfflineSelfCheck.cs` | 真机探针 / 离线自检：凭据服务指向临时目录 + 假加密 |
