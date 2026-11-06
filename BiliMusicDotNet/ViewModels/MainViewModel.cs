using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly IBiliLoginService _loginService;
    private readonly RecommendationPageViewModel _recommendationPageViewModel;
    private readonly AboutPageViewModel _aboutPageViewModel;
    private readonly SettingViewModel _settings;
    private NavItem? _previousNavItem;

    public ObservableCollection<NavItem> AllViews { get; set; } = [];

    /// <summary>设置页的虚拟导航项；不在 <see cref="AllViews"/> 中，不显示在导航栏。</summary>
    public NavItem SettingsNavItem { get; }

    /// <summary>设置页视图模型。</summary>
    public SettingViewModel Settings => _settings;

    /// <summary>设置页是否打开。</summary>
    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>当前导航项；由导航控件双向绑定。</summary>
    [ObservableProperty]
    private NavItem? _selectedNavItem;

    /// <summary>
    /// 由容器创建：日志器与登录服务由依赖注入提供。
    /// </summary>
    public MainViewModel(ILogger<MainViewModel> logger,
        IBiliLoginService loginService,
        RecommendationPageViewModel recommendationPageViewModel,
        AboutPageViewModel aboutPageViewModel,
        SettingViewModel settings)
    {
        _logger = logger;
        _loginService = loginService;
        _recommendationPageViewModel = recommendationPageViewModel;
        _aboutPageViewModel = aboutPageViewModel;
        _settings = settings;

        LoadAllViews();

        SettingsNavItem = new NavItem
        {
            Label = "设置",
            Icon = NavIcon.Resolve(NavIcon.Settings),
            Group = NavGroup.A,
            Content = _settings,
        };

        _aboutPageViewModel.OpenSettingsRequested += (_, _) => OpenSettings();
        _settings.CloseSettingViewRequested += (_, _) => CloseSettings();

        _ = RestoreSessionAsync();

        _logger.LogInformation("主视图模型已创建");
    }

    /// <summary>打开设置页，并记住打开前的导航项。</summary>
    public void OpenSettings()
    {
        if (IsSettingsOpen)
            return;

        _previousNavItem = SelectedNavItem;
        IsSettingsOpen = true;
        SelectedNavItem = SettingsNavItem;
        _logger.LogInformation("设置页已打开");
    }

    /// <summary>关闭设置页，回到打开前的那一页；没有记录时交给导航控件回退首项。</summary>
    public void CloseSettings()
    {
        if (!IsSettingsOpen)
            return;

        IsSettingsOpen = false;
        SelectedNavItem = _previousNavItem;
        _previousNavItem = null;
        _logger.LogInformation("设置页已关闭");
    }

    /// <summary>桌面标题栏齿轮：按当前状态打开或关闭设置页。</summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        if (IsSettingsOpen)
            CloseSettings();
        else
            OpenSettings();
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is not null && ReferenceEquals(value, SettingsNavItem))
        {
            IsSettingsOpen = true;
            return;
        }

        if (IsSettingsOpen)
        {
            IsSettingsOpen = false;
            _previousNavItem = null;
        }
    }

    /// <summary>
    /// 启动时恢复上次的登录态
    /// </summary>
    /// <remarks>
    /// 构造函数不能 await，这里只能 fire-and-forget，所以异常必须自己吞掉——未观测的异常不会有人处理。
    /// 恢复成功时由 <see cref="AboutPageViewModel"/> 订阅的 LoginSucceeded 换头像与用户名。
    /// </remarks>
    private async Task RestoreSessionAsync()
    {
        try
        {
            if (!await _loginService.TryRestoreSessionAsync().ConfigureAwait(true))
            {
                _logger.LogInformation("没有可恢复的登录态");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复登录态失败，本次按未登录启动");
        }
    }

    /// <summary>
    /// 加载所有的 View
    /// </summary>
    private void LoadAllViews()
    {
        AllViews =
        [
            // Recommendation
            new NavItem()
            {
                Label = "推荐",
                Icon = NavIcon.Resolve(NavIcon.Recommendation),
                Group = NavGroup.B,
                Content = _recommendationPageViewModel
            },
            
            // About
            new NavItem()
            {
                Label = "关于",
                Icon = NavIcon.Resolve(NavIcon.About),
                Group = NavGroup.A,
                Content = _aboutPageViewModel
            }
        ];
    }
}
