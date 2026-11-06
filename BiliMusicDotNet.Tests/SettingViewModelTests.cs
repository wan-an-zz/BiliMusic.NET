using BiliMusicDotNet.Models.Settings;
using BiliMusicDotNet.Tests.Support;
using BiliMusicDotNet.ViewModels;
using Microsoft.Extensions.Logging;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 设置页视图模型：分区读取、值变化转交、关闭事件与异常隔离。
/// </summary>
public sealed class SettingViewModelTests
{
    [Fact]
    public void 构造时从服务读取分区()
    {
        var item = Toggle();
        var group = Group(item);
        var service = new FakeSettingService(group);

        var viewModel = new SettingViewModel(service);

        Assert.Same(group, Assert.Single(viewModel.Sections));
    }

    [Fact]
    public void 没有设置项时IsEmpty为真()
    {
        var viewModel = new SettingViewModel(new FakeSettingService());

        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.HasSettings);
    }

    [Fact]
    public void 设置项值变化时调用ChangeSettings()
    {
        var item = Toggle();
        var service = new FakeSettingService(Group(item));
        _ = new SettingViewModel(service);

        item.Value = false;

        Assert.Same(item, Assert.Single(service.ChangedItems));
    }

    [Fact]
    public void 同一项一次变化只调用一次()
    {
        var item = Toggle();
        var service = new FakeSettingService(Group(item));
        _ = new SettingViewModel(service);

        item.Value = false;

        Assert.Single(service.ChangedItems);
    }

    [Fact]
    public void 关闭命令触发CloseRequested()
    {
        var viewModel = new SettingViewModel(new FakeSettingService());
        var raised = 0;
        viewModel.CloseSettingViewRequested += (_, _) => raised++;

        viewModel.CloseCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void 服务抛异常时记录日志且不向UI抛出()
    {
        var item = Toggle();
        var service = new FakeSettingService(Group(item))
        {
            ChangeException = new InvalidOperationException("模拟保存失败"),
        };
        var logger = new RecordingLogger<SettingViewModel>();
        _ = new SettingViewModel(service, logger);

        var exception = Record.Exception(() => item.Value = false);

        Assert.Null(exception);
        Assert.Contains(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public void HasSettings与Sections一致()
    {
        var withItems = new SettingViewModel(new FakeSettingService(Group(Toggle())));
        var empty = new SettingViewModel(new FakeSettingService());

        Assert.True(withItems.HasSettings);
        Assert.False(withItems.IsEmpty);
        Assert.False(empty.HasSettings);
        Assert.True(empty.IsEmpty);
        Assert.Equal(withItems.Sections.Count > 0, withItems.HasSettings);
        Assert.Equal(empty.Sections.Count > 0, empty.HasSettings);
    }

    private static SettingSectionGroup Group(params SettingItem[] items)
        => new(SettingSection.About, "关于设置", 1, items);

    private static SettingItem Toggle(string name = "demo", bool value = true)
    {
        return new SettingItem
        {
            Section = SettingSection.About,
            Method = SettingMethod.Toggle,
            Type = typeof(SettingViewModelTests),
            Name = name,
            Title = name,
            Value = value,
        };
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
