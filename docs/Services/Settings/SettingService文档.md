# 设置服务（SettingService）维护文档

> 本文档对应设置模块的「注册 → 装载 → 变更 → 持久化」链路。
> UI 部分见 [设置页面文档](../../UI/设置页面文档.md)。
>
> 相关代码：
> [ISetting](../../../BiliMusicDotNet/Abstracts/ISetting.cs)、
> [ISettingService](../../../BiliMusicDotNet/Abstracts/ISettingService.cs)、
> [SettingService](../../../BiliMusicDotNet/Services/SettingService.cs)、
> [SettingItem](../../../BiliMusicDotNet/Models/Settings/SettingItem.cs)。

---

## 1. 技术栈

| 关注点 | 选型 | 说明 |
| --- | --- | --- |
| 语言 / 框架 | .NET 10、C# latest | 与主工程一致 |
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` | `ISettingService` 单例；注册方按需注册为 `ISetting` |
| JSON | `System.Text.Json` | camelCase、缩进、中文不转义 |
| 日志 | `Microsoft.Extensions.Logging.ILogger<T>` | 读盘、写盘、注册方异常都只记日志 |
| 测试 | xUnit v3 + `Avalonia.Headless.XUnit` | 服务测试不需要 Avalonia；测试项目统一入口 |

---

## 2. 文件布局

| 文件 | 内容 |
| --- | --- |
| `BiliMusicDotNet/Abstracts/ISetting.cs` | 注册方契约 |
| `BiliMusicDotNet/Abstracts/ISettingService.cs` | 设置读取与变更入口 |
| `BiliMusicDotNet/Models/Settings/SettingSection.cs` | 分区枚举 + `SettingSectionAttribute` |
| `BiliMusicDotNet/Models/Settings/SettingMethod.cs` | `Toggle` / `SingleChoice` |
| `BiliMusicDotNet/Models/Settings/SettingItem.cs` | 设置项模型（含 `SettingOption`） |
| `BiliMusicDotNet/Models/Settings/SettingsDocument.cs` | `settings.json` 的持久化 DTO |
| `BiliMusicDotNet/Services/SettingService.cs` | 服务实现 + `SettingSectionGroup` 记录 |

`SettingSectionGroup` 按项目约定放在 `SettingService.cs` 末尾，命名空间仍是 `BiliMusicDotNet.Models.Settings`。

---

## 3. 契约

### 3.1 分区：`SettingSection`

```csharp
public enum SettingSection
{
    [SettingSection("登录设置", 0)]
    Login,

    [SettingSection("关于设置", 1)]
    About,
}
```

- 显示名与分区排序写在 `SettingSectionAttribute` 上。
- 新增分区时同时补枚举值与特性；缺特性时回退枚举名、排序 0。

### 3.2 编辑方式：`SettingMethod`

```csharp
public enum SettingMethod
{
    Toggle,
    SingleChoice,
}
```

### 3.3 候选值：`SettingOption`

```csharp
public sealed record SettingOption(string Value, string Title);
```

- `Value` 写进 JSON；`Title` 只用于界面。
- 同一个 `SettingItem` 内 `Title` 应唯一，重复时取第一个匹配。

### 3.4 设置项：`SettingItem`

```csharp
public sealed partial class SettingItem : ObservableObject
{
    public required SettingSection Section { get; init; }
    public required SettingMethod Method { get; init; }
    public required Type Type { get; init; }
    public required string Name { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<SettingOption> Options { get; init; }
    public ObservableCollection<string> OptionTitles { get; }
    public object? Value { get; set; }
    public bool BoolValue { get; set; }
    public string SelectedOptionTitle { get; set; }
    public bool IsToggle { get; }
    public bool IsSingleChoice { get; }
    public bool HasDescription { get; }
}
```

约定：

| 属性 | 说明 |
| --- | --- |
| `Type` | **注册方的类型**，变更时按它找回注册方；每个 item 必须填自己 |
| `Name` | 稳定键，持久化与去重都用它；改名会丢值 |
| `Value` | 唯一事实来源，只支持 `bool`（Toggle）与 `string`（SingleChoice） |
| `BoolValue` | Toggle 控件的绑定门面 |
| `SelectedOptionTitle` | 下拉/选择器的绑定门面，按 `Title` 映射回 `Value` |
| `OptionTitles` | 给只接受字符串集合的 CherylUI `FormFieldPicker` 使用 |
| `Order` | 同一分区内排序，升序；再按 `Name` 兜底 |

### 3.5 注册方：`ISetting`

```csharp
public interface ISetting
{
    IEnumerable<SettingItem> RegisterSettings();

    void ReceiveSettingChanges(SettingItem item);
}
```

- `RegisterSettings()` 返回的 `Value` 是默认值。
- `SettingService` 用文件值覆盖后再回调 `ReceiveSettingChanges`。
- 注册方**不得**在 `ReceiveSettingChanges` 里再调用 `ISettingService`，避免 DI 循环与重入。
- 启动装载和用户修改都会调用 `ReceiveSettingChanges`，注册方应把两种来源当成同一件事处理。

### 3.6 服务接口：`ISettingService`

```csharp
public interface ISettingService
{
    IReadOnlyList<SettingSectionGroup> GetAllSettings();

    void ChangeSettings(SettingItem item);
}
```

`SettingSectionGroup`：

```csharp
public sealed record SettingSectionGroup(
    SettingSection Section,
    string Title,
    int Order,
    IReadOnlyList<SettingItem> Items);
```

- `GetAllSettings()` 返回构造期缓存的分区，不重复读盘。
- `ChangeSettings()` 先通知注册方，再同步写盘；异常只记日志，不向 UI 抛出。

### 3.7 实现：`SettingService`

构造重载：

```csharp
public SettingService(IEnumerable<ISetting> registrants, ILogger<SettingService> logger);

public SettingService(
    string? baseDirectory,
    IEnumerable<ISetting> registrants,
    ILogger<SettingService> logger);
```

- 第二个重载供测试显式指定应用数据根目录。
- 静态路径解析：

```csharp
public static string ResolveFilePath(string baseDirectory);
// <baseDirectory>\BiliMusicDotNet\Settings\settings.json
```

构造期流程：

1. 遍历 `IEnumerable<ISetting>`，调用 `RegisterSettings()`；单个注册方抛异常时记日志并跳过。
2. 按 `(Type.FullName, Name)` 去重；重复身份保留第一条并记警告。
3. 读 `settings.json`；文件不存在、损坏、IO 失败都使用默认值。
4. 用文件值覆盖对应 item；类型不匹配或单选值不在 `Options` 中时保留默认值并记警告。
5. 按 `SettingSectionAttribute.Order` 分区、组内按 `Order` 再按 `Name` 排序。
6. 对每个 item 调用对应注册方的 `ReceiveSettingChanges(item)`（含默认值）。

`ChangeSettings(item)`：

1. `ArgumentNullException.ThrowIfNull(item)`。
2. 按 `item.Type` 找注册方；找不到记警告。
3. 找到则调用 `ReceiveSettingChanges(item)`；异常只记日志。
4. 同步写盘；失败只记日志，内存值与 UI 不回滚。

线程：

- 内部集合与写盘用 `lock` 保护。
- 调用注册方时不在锁内，避免注册方回调导致死锁。
- 写盘串行，避免多个设置同时变更时互相覆盖。

---

## 4. 持久化

### 4.1 路径

```text
%LOCALAPPDATA%\BiliMusicDotNet\Settings\settings.json
```

### 4.2 结构

```json
{
  "version": 1,
  "items": [
    {
      "type": "BiliMusicDotNet.ViewModels.AboutPageViewModel",
      "name": "demo.about.toggle",
      "value": true
    }
  ]
}
```

- 属性名 camelCase：`version` / `items` / `type` / `name` / `value`。
- 只保存身份与值；分区、标题、描述、方式、顺序都以代码注册为准。
- 未知 `type` / `name` 的条目原样保留，写回时追加在已知项之后。
- 已知项不重复写未知副本。
- 类型不匹配（Toggle 不是 bool、SingleChoice 不是 string）→ 忽略该条目、保留默认值、记警告。
- `version != 1` → 记警告后仍按当前结构尽力读取。
- 写入：`settings.json.tmp` → `File.Move(..., overwrite: true)`；UTF-8 无 BOM、缩进、中文不转义。

### 4.3 容错

| 场景 | 行为 |
| --- | --- |
| 文件不存在 | 使用注册默认值，不创建文件 |
| JSON 损坏 | 使用默认值，记警告 |
| 读文件 IO 失败 | 使用默认值，记警告 |
| 写盘失败 | 记错误，内存值与界面不回滚 |
| 注册方异常 | 跳过该注册方/该次回调，继续其它项 |
| 重复身份 | 保留第一条，记警告 |

---

## 5. 注册方接入示例

```csharp
using System.Collections.Generic;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Settings;

public sealed class MyFeatureViewModel : ViewModelBase, ISetting
{
    public IEnumerable<SettingItem> RegisterSettings()
    {
        yield return new SettingItem
        {
            Section = SettingSection.About,
            Method = SettingMethod.Toggle,
            Type = typeof(MyFeatureViewModel),
            Name = "my.feature.enabled",
            Title = "启用我的功能",
            Description = "关闭后不再加载该功能。",
            Order = 10,
            Value = true,
        };
    }

    public void ReceiveSettingChanges(SettingItem item)
    {
        if (item.Name == "my.feature.enabled")
            IsEnabled = item.BoolValue;
    }

    public bool IsEnabled { get; private set; }
}
```

DI 注册（组合根）：

```csharp
services.AddSingleton<MyFeatureViewModel>();
services.AddSingleton<ISetting>(sp => sp.GetRequiredService<MyFeatureViewModel>());
services.AddSingleton<ISettingService, SettingService>();
```

注意：

- 需要注册设置的页面 VM 应改为 Singleton，并用同一实例注册为 `ISetting`。
- 生产代码可以没有 `ISetting` 实现；此时设置页显示空态，服务仍正常工作。
- 不要为设置页单独创建服务实例；`SettingViewModel` 与注册方必须共享同一个 `ISettingService` 单例。

---

## 6. 启动与变更数据流

```text
Container
 └─ ISettingService → SettingService（Singleton）
      ├─ IEnumerable<ISetting>
      ├─ RegisterSettings()
      ├─ 读 settings.json
      ├─ 合并值 → 按分区排序
      └─ ReceiveSettingChanges(item)（含默认值）

SettingItem.Value 变化
 └─ SettingViewModel 监听 PropertyChanged
      └─ ISettingService.ChangeSettings(item)
           ├─ 按 item.Type 找注册方
           ├─ ReceiveSettingChanges(item)
           └─ 同步原子写 settings.json
```

`SettingService` 与 `SettingSectionGroup.Items` 共用同一批 `SettingItem` 实例，所以值变化由 item 自己的 `INotifyPropertyChanged` 驱动 UI，不需要把分组集合改成 `ObservableCollection`。

---

## 7. 测试

测试文件：`BiliMusicDotNet.Tests/SettingServiceTests.cs`，共 16 条。

| # | 用例 |
| --- | --- |
| 1 | `GetAllSettings按分区特性顺序分组` |
| 2 | `同一分区内按Order再按Name排序` |
| 3 | `没有设置文件时使用注册默认值` |
| 4 | `载入JSON后覆盖对应设置项的值` |
| 5 | `设置文件损坏时使用默认值且不抛异常` |
| 6 | `未知条目在写回后仍然保留` |
| 7 | `类型不匹配的值被忽略并保留默认值` |
| 8 | `版本号非1时仍尽力读取` |
| 9 | `ChangeSettings只通知匹配的注册方` |
| 10 | `ChangeSettings把新值写回JSON` |
| 11 | `ChangeSettings在注册方抛异常时仍写盘` |
| 12 | `写盘失败时不抛出且保留内存值` |
| 13 | `启动时逐项通知注册方含默认值` |
| 14 | `RegisterSettings抛异常的注册方被跳过` |
| 15 | `ResolveFilePath指向LocalApplicationData下的Settings目录` |
| 16 | `重复身份只保留第一条并记警告` |

测试支撑：`BiliMusicDotNet.Tests/Support/FakeSettingRegistrant.cs`。

运行：

```powershell
dotnet run --project .\BiliMusicDotNet.Tests\BiliMusicDotNet.Tests.csproj -p:UsedAvaloniaProducts=""
# 判定：Total: 155, Errors: 0, Failed: 0
```

---

## 8. 提交对照

分支：`feature/Settings`。

| 提交 | 内容 |
| --- | --- |
| `feat(settings): 新增设置项模型与注册接口` | `SettingSection` / `SettingMethod` / `SettingOption` / `SettingItem` / `ISetting` |
| `feat(settings): 新增设置服务与 JSON 持久化` | `ISettingService`、`SettingService`、`SettingsDocument`、DI 注册 |
| `test(settings): 新增设置服务测试` | `SettingServiceTests`（16 条）+ `FakeSettingRegistrant` |
| `docs(settings): 新增设置服务文档` | 本文档 |

---

## 9. 边界与注意事项

- **不监听外部修改**：只覆盖应用内变更，不监视 `settings.json` 的外部编辑。
- **不实现设置项动态增删**：`Sections` / `Items` 在运行期不变。
- **不支持复杂值**：当前只有 Toggle（bool）与 SingleChoice（string）；新增类型需同时扩展 `SettingMethod`、`TryApply`、UI 模板。
- **同步写盘**：单文件小 JSON，用户选择立即同步原子写；后续若变慢再考虑异步队列。
- **注册方生命周期**：Singleton；Transient 注册方会导致 `SettingService` 与页面使用的不是同一实例，`ReceiveSettingChanges` 收不到实际页面对象。
- **分区特性**：新增分区必须补 `SettingSectionAttribute`，否则回退枚举名、排序 0。
