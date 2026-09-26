using System.Collections.ObjectModel;
using BiliMusicDotNet.Models.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly RecommendationPageViewModel _recommendationPageViewModel;
    private readonly AboutPageViewModel _aboutPageViewModel;

    public ObservableCollection<NavItem> AllViews { get; set; }
    
    /// <summary>
    /// 由容器创建：日志器由依赖注入提供。
    /// </summary>
    public MainViewModel(ILogger<MainViewModel> logger,
        RecommendationPageViewModel recommendationPageViewModel,
        AboutPageViewModel aboutPageViewModel)
    {
        _logger = logger;
        _recommendationPageViewModel = recommendationPageViewModel;
        _aboutPageViewModel = aboutPageViewModel;
        
        LoadAllViews();
        
        
        
        _logger.LogInformation("主视图模型已创建");
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
                Group = NavGroup.A,
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
