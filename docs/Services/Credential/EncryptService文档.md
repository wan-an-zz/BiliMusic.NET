# 凭据加密服务（IEncryptService）维护文档

> 本文档对应"登录凭据加密落盘"这件事里**加密本身**的部分：契约、三端实现与密钥约定。
> 落盘时机、失败兜底与文件布局由 [登录凭据服务文档](LoginCredentialService文档.md) 负责。
>
> 相关文档：[登录凭据服务文档](LoginCredentialService文档.md)、
> [登录服务文档](../Login/LoginService文档.md)、
> [Bilibili API 参考 §7.6 会话持久化与安全](../../bili_api/bilibili-api-reference.md#76-会话持久化与安全)

---

## 1. 技术栈

| 关注点     | 选型 | 说明 |
|------------| --- | --- |
| 接口位置   | `BiliMusicDotNet/Abstracts/IEncryptService.cs` | 共享项目，与 `ICredentialService` 同层 |
| 方法形态   | 同步：`byte[] Encrypt(Stream)` / `byte[] Decrypt(Stream)` | 两个平台的原语都是同步的；异步包装只会把线程池拉进来 |
| 数据形态   | 流入、`byte[]` 出 | 调用方已有内存里的 JSON 字节，不需要流式加密 |
| 桌面端     | DPAPI（`System.Security.Cryptography.ProtectedData`） | `DataProtectionScope.CurrentUser` + 固定附加熵；Windows 专用 |
| Android 端 | 系统密钥库（`AndroidKeyStore`）+ AES-256-GCM | 密钥不出安全硬件；别名 `bili_login_credential` |
| 异常契约   | `CryptographicException` / `PlatformNotSupportedException` | 调用方只认这两种，不必理解平台异常体系 |
| 测试       | xUnit v3（`xunit.v3` 3.2.2） | `EncryptServiceTests` 直接编译桌面端实现源码，见 §2.2 |

---

## 2. 子功能、提交与测试

### 2.1 提交对照表

| 提交 | 内容 |
| --- | --- |
| （待提交）`feat(credential): 凭据落盘改为平台加密` | 契约、`DpapiEncryptService`、`AndroidKeystoreEncryptService`、两端 DI 注册 |
| （待提交）`test(credential): 凭据加密测试用例` | `EncryptServiceTests`、`FakeEncryptService` 与共享层测试的适配 |

分支：`feature/Login`。

### 2.2 测试入口（提交闸门）

```powershell
dotnet run --project .\BiliMusicDotNet.Tests\BiliMusicDotNet.Tests.csproj -- -class "*EncryptServiceTests"
# 判定依据：Total: 6, Errors: 0, Failed: 0
```

桌面端实现靠 `<Compile Include>` 链接进测试项目编译（见 `BiliMusicDotNet.Tests.csproj`）：
桌面端是带入口点的 WinExe 应用头，为测一个加密类把 Avalonia.Desktop / SukiUI / NLog 全拖进来不划算。
链接的仍是同一份源码，桌面端改了测试就会跟着变。

### 2.3 子功能与测试对照表

| # | 子功能 | 实现位置 | 测试 | 用例数 |
| --- | --- | --- | --- | --- |
| 1 | 加密 → 解密可还原 | `DpapiEncryptService.Encrypt` / `Decrypt` | `EncryptServiceTests.加密后能解回原文` | 1 |
| 2 | 密文不含明文 | 同上 | `EncryptServiceTests.密文与明文不同且看不出原文` | 1 |
| 3 | 同一明文两次加密结果不同 | 同上 | `EncryptServiceTests.同一份明文两次加密得到不同密文` | 1 |
| 4 | 非本应用密文的失败契约 | `DpapiEncryptService.Decrypt` | `EncryptServiceTests.不是本应用加密出来的字节解不开` | 1 |
| 5 | 流的读取起点约定 | `DpapiEncryptService.Protect` | `EncryptServiceTests.从流的当前位置开始读` | 1 |
| 6 | 与凭据服务串起来的端到端链路 | `CredentialService` + `DpapiEncryptService` | `EncryptServiceTests.与凭据服务串起来能密文落盘并原样读回` | 1 |
| 7 | 共享层对加密的调用 | `CredentialService` | `CredentialStorageTests.交给加密服务的是凭据JSON明文` / `解密失败时*` / `加密失败时*` | 3 |

Android 端实现没有自动化用例：它要真机上的系统密钥库，测试宿主用不了。
验证方式是编译期（`dotnet build` 的 AVLN / CS 判定，见 AGENTS.md）与真机安装后的登录落盘。

---

## 3. 契约

```csharp
public interface IEncryptService
{
    byte[] Encrypt(Stream plaintext);
    byte[] Decrypt(Stream ciphertext);
}
```

| 约定 | 内容 |
| --- | --- |
| 读取范围 | 从流的**当前位置**读到末尾；调用方不必先归零 `Position` |
| 密钥来源 | 平台自己保管，接口不接受调用方传入密钥——传密钥的口子迟早会被用来硬编码密钥 |
| 加密失败 | 抛 `CryptographicException` |
| 解密失败 | 抛 `CryptographicException`（换机器、换密钥、文件损坏都归这一类） |
| 平台不支持 | 抛 `PlatformNotSupportedException` |
| 线程安全 | 要求实现可被并发调用；`CredentialService` 是 Singleton |

调用方（`CredentialService`）对这两类异常的处理不同，这也是契约里只有两种异常的原因：

| 异常 | `SaveAsync` | `LoadAsync` |
| --- | --- | --- |
| `CryptographicException` / `PlatformNotSupportedException` | 包成 `StorageException` 抛出 | 记 warning，返回 `false`（视为未登录） |

---

## 4. 平台实现

### 4.1 桌面端：DPAPI

```csharp
ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
```

- **作用域 `CurrentUser`**：密钥由系统按当前用户派生。同一台机器换个 Windows 账号就解不开，这是有意的。
- **附加熵**：`"BiliMusicDotNet.LoginCredential"` 的 UTF-8 字节。它**不是秘密**（就写在源码里），只是给密文加一层应用隔离，避免同一用户下的其它程序直接调用 `Unprotect` 就能解开。
- **改动附加熵等于作废已有密文**：老用户会表现为"需要重新扫码"。
- **包引用**：`System.Security.Cryptography.ProtectedData` 不在共享框架里，桌面端项目单独引了它（版本见 `Directory.Packages.props`）。
- **目标框架**：桌面端项目因此只面向 `net10.0-windows`。留着可移植的 `net10.0` 只会让"能编译但一登录就崩"成为可能。

### 4.2 Android 端：系统密钥库 + AES-GCM

密钥由系统密钥库生成并保管（`SecretKey`，不是非对称密钥对——理由见下面的"不用 RSA"）：

```csharp
KeyGenParameterSpec.Builder("bili_login_credential", KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
    .SetBlockModes(KeyProperties.BlockModeGcm)
    .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
    .SetKeySize(256)
```

- **别名**：`bili_login_credential`。密钥在第一次保存凭据时生成，之后一直复用。
- **密钥材料不出密钥库**：应用只能通过别名请求加解密，拿不到密钥本身；有硬件支持时由 TEE / StrongBox 承载。
- **不用 RSA**：2048 位 RSA 单次只能加密 245 字节，而凭据 JSON（含 `cookieHeader`）约 300~400 字节，一上线就会 `IllegalBlockSizeException`。GCM 是对称认证加密，长度不受限，还顺带保证了密文完整性。
- **密文布局**：`[12 字节 IV][密文 + 16 字节认证标签]`。IV 每次加密都重新随机生成（GCM 复用 IV 会直接摧毁安全性），所以必须随密文一起存。
- **IV 由本类生成而非 `Cipher.GetIV()`**：长度必须由本类说了算，解密端才能按固定偏移切分。
- **认证失败即抛**：密文被改动或密钥换了，`DoFinal` 抛 `AEADBadTagException`，被翻译成 `CryptographicException`。
- **最低 API 23**：`KeyGenParameterSpec` 与密钥库中的 AES 都要求 Android 6.0；项目已设 `SupportedOSPlatformVersion=23`。

## 5. 新增一个平台实现要做什么

1. 在平台 head 的 `Services/` 下实现 `IEncryptService`，密钥**不要**由调用方传入；
2. 失败一律翻译成 `System.Security.Cryptography.CryptographicException`，平台异常体系不外泄；
3. 在平台组合根注册：`services.AddSingleton<IEncryptService, /* 你的实现 */>()`；
4. 该平台的容器开了 `ValidateOnBuild`，漏注册会在启动时立刻抛出，不会拖到登录那一刻。

不需要改动的部分：`IEncryptService` 契约、`CredentialService`、登录服务、任何视图模型。

---

## 6. 最小使用示例

### 6.1 共享层（`CredentialService` 内部，已是现状）

```csharp
// 保存：序列化 → 加密 → 原子写
var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, CredentialJsonContext.Default.BiliLoginCredentials);
byte[] ciphertext;
using (var stream = new MemoryStream(plaintext, writable: false))
{
    ciphertext = _encryptService.Encrypt(stream);
}
await File.WriteAllBytesAsync(temporaryPath, ciphertext, cancellationToken);
File.Move(temporaryPath, _filePath, overwrite: true);
```

```csharp
// 读取：读文件 → 解密 → 反序列化
var ciphertext = await File.ReadAllBytesAsync(_filePath, cancellationToken);
using var stream = new MemoryStream(ciphertext, writable: false);
var credentials = JsonSerializer.Deserialize(
    _encryptService.Decrypt(stream),
    CredentialJsonContext.Default.BiliLoginCredentials);
```

### 6.2 自己写一个实现时（例如给桌面端做 Linux 版本）

```csharp
public sealed class SecretServiceEncryptService : IEncryptService
{
    public byte[] Encrypt(Stream plaintext) => Transform(plaintext, encrypt: true);
    public byte[] Decrypt(Stream ciphertext) => Transform(ciphertext, encrypt: false);

    private static byte[] Transform(Stream payload, bool encrypt)
    {
        try
        {
            // ……调用平台原语……
        }
        catch (SomePlatformException ex)
        {
            // 平台异常不外泄：调用方只按 CryptographicException 兜底。
            throw new CryptographicException("凭据加解密失败", ex);
        }
    }
}
```

---

## 7. 安全边界

| 结论 | 说明 |
| --- | --- |
| 防的是什么 | 磁盘被拷走、被别的程序读走、被误传给别人：这些场景下拿到的是解不开的密文 |
| 不防什么 | 已经能在本机以当前用户身份运行任意代码的攻击者——它可以调用同一个 DPAPI / 密钥库接口解开密文 |
| 与"加密就能少记日志"无关 | 日志依然只允许出现路径、`mid` 与字段是否齐备，**绝不允许出现凭据值** |
| 内存里仍是明文 | `Current` 持有的就是明文凭据，这是登录所必需的；本服务只负责"落盘不落明文" |
