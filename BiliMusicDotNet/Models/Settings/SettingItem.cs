using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliMusicDotNet.Models.Settings;

/// <summary>
/// 一个设置项的元数据与当前值；由注册方创建，设置页读取元数据并双向绑定值。
/// </summary>
public sealed partial class SettingItem : ObservableObject
{
    /// <summary>所属分区。</summary>
    public required SettingSection Section { get; init; }

    /// <summary>编辑方式。</summary>
    public required SettingMethod Method { get; init; }

    /// <summary>注册方类型；变更时按它找回注册方。</summary>
    public required Type Type { get; init; }

    /// <summary>稳定键，持久化与去重都依赖它，改名会丢值。</summary>
    public required string Name { get; init; }

    /// <summary>界面显示名。</summary>
    public required string Title { get; init; }

    /// <summary>可选的补充说明。</summary>
    public string? Description { get; init; }

    /// <summary>同一分区内的排序权重，升序。</summary>
    public int Order { get; init; }

    private IReadOnlyList<SettingOption> _options = [];

    /// <summary>单选项的候选值；开关项为空集合。</summary>
    public IReadOnlyList<SettingOption> Options
    {
        get => _options;
        init
        {
            _options = value ?? [];
            OptionTitles = new ObservableCollection<string>(_options.Select(option => option.Title));
        }
    }

    /// <summary>
    /// 候选值的显示文案；桌面下拉与 Android 选择器直接绑定它。
    /// </summary>
    /// <remarks>值到文案的映射以 <see cref="Options"/> 为准，这里只是给只接受字符串集合的控件用。</remarks>
    public ObservableCollection<string> OptionTitles { get; private set; } = [];

    /// <summary>当前值，也是唯一事实来源。</summary>
    [ObservableProperty]
    private object? _value;

    /// <summary>开关控件的绑定门面，读写都落到 <see cref="Value"/>。</summary>
    public bool BoolValue
    {
        get => Value is true;
        set => Value = value;
    }

    /// <summary>下拉/选择器控件的绑定门面，按标题映射到 <see cref="Value"/>。</summary>
    public string SelectedOptionTitle
    {
        get => Options.FirstOrDefault(option =>
            string.Equals(option.Value, Value as string, StringComparison.Ordinal))?.Title ?? string.Empty;
        set
        {
            var option = Options.FirstOrDefault(candidate =>
                string.Equals(candidate.Title, value, StringComparison.Ordinal));
            if (option is not null)
                Value = option.Value;
        }
    }

    /// <summary>是否渲染为开关。</summary>
    public bool IsToggle => Method == SettingMethod.Toggle;

    /// <summary>是否渲染为单选项。</summary>
    public bool IsSingleChoice => Method == SettingMethod.SingleChoice;

    /// <summary>是否显示 <see cref="Description"/>。</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    partial void OnValueChanged(object? value)
    {
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SelectedOptionTitle));
    }
}

/// <summary>
/// 单选项的一个候选值。
/// </summary>
/// <param name="Value">持久化到设置文件的值。</param>
/// <param name="Title">界面上显示的文案。</param>
public sealed record SettingOption(string Value, string Title);
