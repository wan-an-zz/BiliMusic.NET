using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Settings;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.ViewModels;

/// <summary>
/// 设置页：把服务装载好的分区直接交给视图，并把设置项的值变化转交服务保存。
/// </summary>
public sealed partial class SettingViewModel : ViewModelBase
{
    private readonly ISettingService _settingService;
    private readonly ILogger<SettingViewModel> _logger;
    private bool _isProcessingChange;

    /// <summary>
    /// 由容器创建。
    /// </summary>
    /// <param name="settingService">设置读取与保存入口。</param>
    /// <param name="logger">日志器。</param>
    public SettingViewModel(ISettingService settingService, ILogger<SettingViewModel>? logger = null)
    {
        _settingService = settingService ?? throw new ArgumentNullException(nameof(settingService));
        _logger = logger ?? NullLogger<SettingViewModel>.Instance;
        Sections = settingService.GetAllSettings();

        foreach (var item in Sections.SelectMany(group => group.Items))
            item.PropertyChanged += OnSettingItemPropertyChanged;
    }

    /// <summary>按分区打包好的设置项；运行期不增删。</summary>
    public IReadOnlyList<SettingSectionGroup> Sections { get; }

    /// <summary>是否有可显示的设置项。</summary>
    public bool HasSettings => Sections.Count > 0;

    /// <summary>是否显示空态提示。</summary>
    public bool IsEmpty => !HasSettings;

    /// <summary>用户请求关闭设置页；由主视图模型负责切回上一页。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>请求关闭设置页。</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isProcessingChange || e.PropertyName != nameof(SettingItem.Value) || sender is not SettingItem item)
            return;

        _isProcessingChange = true;
        try
        {
            _settingService.ChangeSettings(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置项保存失败：{Name}", item.Name);
        }
        finally
        {
            _isProcessingChange = false;
        }
    }
}
