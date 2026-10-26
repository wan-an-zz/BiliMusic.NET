using System.IO;
using System.Text.Json;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Settings;
using BiliMusicDotNet.Services;
using BiliMusicDotNet.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 设置服务：分区排序、JSON 合并与保留、变更通知与写盘容错。
/// </summary>
public sealed class SettingServiceTests
{
    [Fact]
    public void GetAllSettings按分区特性顺序分组()
    {
        using var directory = new TempDirectory();
        var login = Toggle("login.item", section: SettingSection.Login);
        var about = Toggle("about.item", section: SettingSection.About);
        var service = CreateService(directory, new FakeSettingRegistrant(login, about));

        var groups = service.GetAllSettings();

        Assert.Equal(
            new[] { SettingSection.Login, SettingSection.About },
            groups.Select(group => group.Section).ToArray());
        Assert.Equal(new[] { "登录设置", "关于设置" }, groups.Select(group => group.Title).ToArray());
    }

    [Fact]
    public void 同一分区内按Order再按Name排序()
    {
        using var directory = new TempDirectory();
        var beta = Toggle("beta", order: 0);
        var alpha = Toggle("alpha", order: 1);
        var zulu = Toggle("zulu", order: 1);
        var service = CreateService(directory, new FakeSettingRegistrant(beta, alpha, zulu));

        var group = Assert.Single(service.GetAllSettings());

        Assert.Equal(new[] { "beta", "alpha", "zulu" }, group.Items.Select(item => item.Name).ToArray());
    }

    [Fact]
    public void 没有设置文件时使用注册默认值()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        var service = CreateService(directory, new FakeSettingRegistrant(item));

        var group = Assert.Single(service.GetAllSettings());

        Assert.True(Assert.IsType<bool>(Assert.Single(group.Items).Value));
        Assert.False(File.Exists(SettingsPath(directory)));
    }

    [Fact]
    public void 载入JSON后覆盖对应设置项的值()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        WriteDocument(directory, 1, Entry(item.Type, item.Name, false));
        var service = CreateService(directory, new FakeSettingRegistrant(item));

        var group = Assert.Single(service.GetAllSettings());

        Assert.False(Assert.IsType<bool>(Assert.Single(group.Items).Value));
    }

    [Fact]
    public void 设置文件损坏时使用默认值且不抛异常()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        WriteRaw(directory, "{not json");
        var service = CreateService(directory, new FakeSettingRegistrant(item));

        var group = Assert.Single(service.GetAllSettings());

        Assert.True(Assert.IsType<bool>(Assert.Single(group.Items).Value));
    }

    [Fact]
    public void 未知条目在写回后仍然保留()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        WriteDocument(
            directory,
            1,
            Entry(item.Type, item.Name, true),
            Entry(typeof(string), "unknown", 42));
        var service = CreateService(directory, new FakeSettingRegistrant(item));

        item.Value = false;
        service.ChangeSettings(item);

        var items = ReadItems(directory);
        var unknown = items.EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == "unknown");
        Assert.Equal("System.String", unknown.GetProperty("type").GetString());
        Assert.Equal(42, unknown.GetProperty("value").GetInt32());
        Assert.False(FindEntry(items, item.Type, item.Name).GetProperty("value").GetBoolean());
    }

    [Fact]
    public void 类型不匹配的值被忽略并保留默认值()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        WriteDocument(directory, 1, Entry(item.Type, item.Name, "not-a-bool"));
        var service = CreateService(directory, new FakeSettingRegistrant(item));

        var group = Assert.Single(service.GetAllSettings());

        Assert.True(Assert.IsType<bool>(Assert.Single(group.Items).Value));
    }

    [Fact]
    public void 版本号非1时仍尽力读取()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        WriteDocument(directory, 2, Entry(item.Type, item.Name, false));
        var service = CreateService(directory, new FakeSettingRegistrant(item));

        var group = Assert.Single(service.GetAllSettings());

        Assert.False(Assert.IsType<bool>(Assert.Single(group.Items).Value));
    }

    [Fact]
    public void ChangeSettings只通知匹配的注册方()
    {
        using var directory = new TempDirectory();
        var firstItem = Toggle("first", type: typeof(FakeSettingRegistrant));
        var secondItem = Toggle("second", type: typeof(OtherRegistrantType));
        var first = new FakeSettingRegistrant(firstItem);
        var second = new FakeSettingRegistrant(secondItem);
        var service = CreateService(directory, first, second);
        first.Received.Clear();
        second.Received.Clear();

        firstItem.Value = false;
        service.ChangeSettings(firstItem);

        Assert.Same(firstItem, Assert.Single(first.Received));
        Assert.Empty(second.Received);
    }

    [Fact]
    public void ChangeSettings把新值写回JSON()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        var service = CreateService(directory, new FakeSettingRegistrant(item));

        item.Value = false;
        service.ChangeSettings(item);

        var entry = FindEntry(ReadItems(directory), item.Type, item.Name);
        Assert.False(entry.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void ChangeSettings在注册方抛异常时仍写盘()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        var registrant = new FakeSettingRegistrant(item)
        {
            ReceiveException = new InvalidOperationException("模拟注册方失败"),
        };
        var service = CreateService(directory, registrant);

        item.Value = false;
        var exception = Record.Exception(() => service.ChangeSettings(item));

        Assert.Null(exception);
        var entry = FindEntry(ReadItems(directory), item.Type, item.Name);
        Assert.False(entry.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void 写盘失败时不抛出且保留内存值()
    {
        using var directory = new TempDirectory();
        var item = Toggle("demo", true);
        var service = CreateService(directory, new FakeSettingRegistrant(item));
        Directory.CreateDirectory(SettingsPath(directory));

        item.Value = false;
        var exception = Record.Exception(() => service.ChangeSettings(item));

        Assert.Null(exception);
        Assert.False(Assert.IsType<bool>(item.Value));
    }

    [Fact]
    public void 启动时逐项通知注册方含默认值()
    {
        using var directory = new TempDirectory();
        var first = Toggle("first", true);
        var second = Toggle("second", false);
        var registrant = new FakeSettingRegistrant(first, second);

        _ = CreateService(directory, registrant);

        Assert.Equal(2, registrant.Received.Count);
        Assert.Same(first, registrant.Received[0]);
        Assert.Same(second, registrant.Received[1]);
    }

    [Fact]
    public void RegisterSettings抛异常的注册方被跳过()
    {
        using var directory = new TempDirectory();
        var badItem = Toggle("bad", true);
        var goodItem = Toggle("good", true);
        var bad = new FakeSettingRegistrant(badItem)
        {
            RegisterException = new InvalidOperationException("模拟注册失败"),
        };
        var good = new FakeSettingRegistrant(goodItem);

        var service = CreateService(directory, bad, good);

        var group = Assert.Single(service.GetAllSettings());
        Assert.Equal("good", Assert.Single(group.Items).Name);
    }

    [Fact]
    public void ResolveFilePath指向LocalApplicationData下的Settings目录()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "BiliMusicDotNet.Tests", "base");
        var expected = Path.Combine(baseDirectory, "BiliMusicDotNet", "Settings", "settings.json");

        Assert.Equal(expected, SettingService.ResolveFilePath(baseDirectory));
    }

    [Fact]
    public void 重复身份只保留第一条并记警告()
    {
        using var directory = new TempDirectory();
        var firstItem = Toggle("duplicate", true);
        var secondItem = Toggle("duplicate", false);
        var first = new FakeSettingRegistrant(firstItem);
        var second = new FakeSettingRegistrant(secondItem);

        var service = CreateService(directory, first, second);

        var group = Assert.Single(service.GetAllSettings());
        Assert.Same(firstItem, Assert.Single(group.Items));
        Assert.Single(first.Received);
        Assert.Empty(second.Received);
    }

    private sealed class OtherRegistrantType
    {
    }

    private static SettingService CreateService(TempDirectory directory, params ISetting[] registrants)
        => new(directory.Path, registrants, NullLogger<SettingService>.Instance);

    private static SettingItem Toggle(
        string name,
        bool value = true,
        SettingSection section = SettingSection.About,
        int order = 0,
        Type? type = null)
    {
        return new SettingItem
        {
            Section = section,
            Method = SettingMethod.Toggle,
            Type = type ?? typeof(FakeSettingRegistrant),
            Name = name,
            Title = name,
            Order = order,
            Value = value,
        };
    }

    private static string SettingsPath(TempDirectory directory)
        => SettingService.ResolveFilePath(directory.Path);

    private static void WriteRaw(TempDirectory directory, string json)
    {
        var path = SettingsPath(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static void WriteDocument(TempDirectory directory, int version, params object[] items)
        => WriteRaw(directory, JsonSerializer.Serialize(new { version, items }));

    private static object Entry(Type type, string name, object? value)
        => new { type = type.FullName, name, value };

    private static JsonElement ReadItems(TempDirectory directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath(directory)));
        return document.RootElement.GetProperty("items").Clone();
    }

    private static JsonElement FindEntry(JsonElement items, Type type, string name)
        => items.EnumerateArray().Single(entry =>
            entry.GetProperty("type").GetString() == type.FullName &&
            entry.GetProperty("name").GetString() == name);
}
