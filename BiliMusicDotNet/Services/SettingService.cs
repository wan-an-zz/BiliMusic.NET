using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Settings;
using Microsoft.Extensions.Logging;

namespace BiliMusicDotNet.Services
{
    /// <summary>
    /// <see cref="ISettingService"/> 的默认实现：构造时收集注册项、合并本地 JSON，之后同步写回。
    /// </summary>
    /// <remarks>
    /// 读盘、写盘与通知注册方都不向调用方抛异常；失败只记日志，界面继续使用内存值。
    /// 写盘先落临时文件再原子替换，避免进程中断留下半个 JSON。
    /// </remarks>
    public sealed class SettingService : ISettingService
    {
        private const string AppFolderName = "BiliMusicDotNet";
        private const string SettingsFolderName = "Settings";
        private const string SettingsFileName = "settings.json";
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly ILogger<SettingService> _logger;
        private readonly string _filePath;
        private readonly List<ISetting> _registrants;
        private readonly Dictionary<Type, ISetting> _registrantsByType = [];
        private readonly List<SettingItem> _items = [];
        private readonly List<SettingsEntry> _unknownEntries = [];
        private readonly object _gate = new();
        private IReadOnlyList<SettingSectionGroup> _groups = [];

        /// <summary>
        /// 由容器创建：应用数据目录取操作系统约定位置。
        /// </summary>
        /// <param name="registrants">所有设置注册方；没有实现时传空集合。</param>
        /// <param name="logger">日志器。</param>
        public SettingService(IEnumerable<ISetting> registrants, ILogger<SettingService> logger)
            : this(null, registrants, logger)
        {
        }

        /// <summary>
        /// 完整构造：显式指定应用数据根目录（测试用它避开真实用户目录）。
        /// </summary>
        /// <param name="baseDirectory">应用数据根目录；<see langword="null"/> 时用本地应用数据目录。</param>
        /// <param name="registrants">所有设置注册方。</param>
        /// <param name="logger">日志器。</param>
        /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> 是空白字符串。</exception>
        /// <exception cref="ArgumentNullException"><paramref name="registrants"/> 或 <paramref name="logger"/> 为 <see langword="null"/>。</exception>
        public SettingService(string? baseDirectory, IEnumerable<ISetting> registrants, ILogger<SettingService> logger)
        {
            if (baseDirectory is not null && string.IsNullOrWhiteSpace(baseDirectory))
                throw new ArgumentException("应用数据根目录不能是空白字符串", nameof(baseDirectory));

            ArgumentNullException.ThrowIfNull(registrants);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _registrants = registrants.ToList();
            _filePath = ResolveFilePath(baseDirectory ?? DefaultBaseDirectory());

            Load();
        }

        /// <inheritdoc />
        public IReadOnlyList<SettingSectionGroup> GetAllSettings() => _groups;

        /// <inheritdoc />
        public void ChangeSettings(SettingItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            NotifyRegistrant(item);
            Persist();
        }

        /// <summary>
        /// 某个应用数据根目录下设置文件的完整路径。
        /// </summary>
        /// <param name="baseDirectory">应用数据根目录。</param>
        /// <returns>形如 <c>&lt;baseDirectory&gt;\BiliMusicDotNet\Settings\settings.json</c> 的路径。</returns>
        public static string ResolveFilePath(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                throw new ArgumentException("应用数据根目录不能是空白字符串", nameof(baseDirectory));

            return Path.Combine(baseDirectory, AppFolderName, SettingsFolderName, SettingsFileName);
        }

        private void Load()
        {
            CollectRegistrations();
            Merge(ReadSettingsFile());
            _groups = BuildGroups();

            foreach (var item in _items)
                NotifyRegistrant(item);

            _logger.LogInformation(
                "设置已装载：分区={SectionCount}，项={ItemCount}，未知条目={UnknownCount}，文件={Path}",
                _groups.Count, _items.Count, _unknownEntries.Count, _filePath);
        }

        private void CollectRegistrations()
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);

            foreach (var registrant in _registrants)
            {
                List<SettingItem> registered;
                try
                {
                    registered = registrant.RegisterSettings().ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "注册方声明设置项失败，已跳过");
                    continue;
                }

                if (registered.Count == 0)
                    continue;

                foreach (var item in registered)
                {
                    if (string.IsNullOrWhiteSpace(item.Name))
                    {
                        _logger.LogWarning("注册方返回了无效设置项，已跳过：{Type}", item.Type.FullName);
                        continue;
                    }

                    _registrantsByType.TryAdd(item.Type, registrant);
                    var identity = Identity(item.Type, item.Name);
                    if (!identities.Add(identity))
                    {
                        _logger.LogWarning("设置项身份重复，保留第一条：{Type}.{Name}", item.Type.FullName, item.Name);
                        continue;
                    }

                    _items.Add(item);
                }
            }
        }

        private List<SettingsEntry> ReadSettingsFile()
        {
            if (!File.Exists(_filePath))
                return [];

            try
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                var document = JsonSerializer.Deserialize<SettingsDocument>(json, JsonOptions);
                if (document is null)
                {
                    _logger.LogWarning("设置文件为空或格式不符，使用注册默认值：{Path}", _filePath);
                    return [];
                }

                if (document.Version != CurrentVersion)
                {
                    _logger.LogWarning(
                        "设置文件版本为 {Version}，当前版本为 {CurrentVersion}，将尽力读取：{Path}",
                        document.Version, CurrentVersion, _filePath);
                }

                return document.Items ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "设置文件无法读取，使用注册默认值：{Path}", _filePath);
                return [];
            }
        }

        private void Merge(List<SettingsEntry> savedEntries)
        {
            var itemsByIdentity = _items.ToDictionary(
                item => Identity(item.Type, item.Name),
                StringComparer.Ordinal);
            var applied = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in savedEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.Type) || string.IsNullOrWhiteSpace(entry.Name))
                {
                    _logger.LogWarning("设置文件包含缺少身份信息的条目，已跳过");
                    continue;
                }

                var identity = Identity(entry.Type, entry.Name);
                if (!itemsByIdentity.TryGetValue(identity, out var item))
                {
                    _unknownEntries.Add(entry);
                    continue;
                }

                if (applied.Contains(identity))
                    continue;

                if (TryApply(item, entry.Value))
                    applied.Add(identity);
            }
        }

        private bool TryApply(SettingItem item, JsonElement value)
        {
            switch (item.Method)
            {
                case SettingMethod.Toggle when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    item.Value = value.GetBoolean();
                    return true;

                case SettingMethod.SingleChoice when value.ValueKind == JsonValueKind.String:
                    var saved = value.GetString();
                    if (saved is not null &&
                        item.Options.Any(option => string.Equals(option.Value, saved, StringComparison.Ordinal)))
                    {
                        item.Value = saved;
                        return true;
                    }

                    _logger.LogWarning(
                        "设置项保存的值不在候选项中，保留默认值：{Type}.{Name}={Value}",
                        item.Type.FullName, item.Name, saved);
                    return false;

                default:
                    _logger.LogWarning(
                        "设置项保存的值类型不匹配，保留默认值：{Type}.{Name}，实际类型={Kind}",
                        item.Type.FullName, item.Name, value.ValueKind);
                    return false;
            }
        }

        private IReadOnlyList<SettingSectionGroup> BuildGroups()
        {
            return _items
                .GroupBy(item => item.Section)
                .Select(group =>
                {
                    var metadata = SectionMetadata(group.Key);
                    var items = group
                        .OrderBy(item => item.Order)
                        .ThenBy(item => item.Name, StringComparer.Ordinal)
                        .ToArray();

                    return new SettingSectionGroup(group.Key, metadata.Title, metadata.Order, items);
                })
                .OrderBy(group => group.Order)
                .ThenBy(group => group.Section)
                .ToArray();
        }

        private static (string Title, int Order) SectionMetadata(SettingSection section)
        {
            var attribute = typeof(SettingSection)
                .GetField(section.ToString())
                ?.GetCustomAttribute<SettingSectionAttribute>();

            return attribute is null
                ? (section.ToString(), 0)
                : (attribute.Title, attribute.Order);
        }

        private void NotifyRegistrant(SettingItem item)
        {
            if (!_registrantsByType.TryGetValue(item.Type, out var registrant))
            {
                _logger.LogWarning("找不到设置注册方：{Type}", item.Type.FullName);
                return;
            }

            try
            {
                registrant.ReceiveSettingChanges(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置注册方处理变更失败：{Type}", item.Type.FullName);
            }
        }

        private void Persist()
        {
            lock (_gate)
            {
                try
                {
                    var document = BuildDocument();
                    var directory = Path.GetDirectoryName(_filePath)!;
                    var temporaryPath = _filePath + ".tmp";

                    Directory.CreateDirectory(directory);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
                    File.WriteAllBytes(temporaryPath, bytes);
                    File.Move(temporaryPath, _filePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "设置写入失败：{Path}", _filePath);
                }
            }
        }

        private SettingsDocument BuildDocument()
        {
            var entries = new List<SettingsEntry>(_items.Count + _unknownEntries.Count);

            foreach (var item in _items)
            {
                entries.Add(new SettingsEntry
                {
                    Type = item.Type.FullName ?? item.Type.Name,
                    Name = item.Name,
                    Value = JsonSerializer.SerializeToElement(item.Value, JsonOptions),
                });
            }

            foreach (var entry in _unknownEntries)
            {
                entries.Add(new SettingsEntry
                {
                    Type = entry.Type,
                    Name = entry.Name,
                    Value = entry.Value.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement<object?>(null, JsonOptions)
                        : entry.Value,
                });
            }

            return new SettingsDocument { Version = CurrentVersion, Items = entries };
        }

        private static string Identity(Type type, string name)
            => $"{type.FullName ?? type.Name}\u001f{name}";

        private static string Identity(string type, string name)
            => $"{type}\u001f{name}";

        private static string DefaultBaseDirectory()
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path;
        }
    }
}

namespace BiliMusicDotNet.Models.Settings
{
    /// <summary>
    /// 同一分区的设置项集合，供设置页直接绑定。
    /// </summary>
    public sealed record SettingSectionGroup(
        SettingSection Section,
        string Title,
        int Order,
        IReadOnlyList<SettingItem> Items);
}
