using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using BiliMusicDotNet.Abstracts;
using BiliMusicDotNet.Models.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly IBiliLoginService _loginService;
    private readonly RecommendationPageViewModel _recommendationPageViewModel;
    private readonly AboutPageViewModel _aboutPageViewModel;

    public ObservableCollection<NavItem> AllViews { get; set; }
    
    /// <summary>
    /// 由容器创建：日志器与登录服务由依赖注入提供。
    /// </summary>
    public MainViewModel(ILogger<MainViewModel> logger,
        IBiliLoginService loginService,
        RecommendationPageViewModel recommendationPageViewModel,
        AboutPageViewModel aboutPageViewModel)
    {
        _logger = logger;
        _loginService = loginService;
        _recommendationPageViewModel = recommendationPageViewModel;
        _aboutPageViewModel = aboutPageViewModel;
        
        LoadAllViews();
        
        _ = RestoreSessionAsync();
        
        _logger.LogInformation("主视图模型已创建");
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
