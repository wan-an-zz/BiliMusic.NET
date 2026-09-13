using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiliMusicDotNet.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty] private string _greeting = "Welcome to Avalonia!";

    /// <summary>
    /// 供 XAML 设计器实例化；运行期一律由容器创建。
    /// </summary>
    public MainViewModel() : this(NullLogger<MainViewModel>.Instance)
    {
    }

    /// <summary>
    /// 由容器创建：日志器由依赖注入提供。
    /// </summary>
    /// <param name="logger">该视图模型的日志器。</param>
    public MainViewModel(ILogger<MainViewModel> logger)
    {
        _logger = logger;
        _logger.LogInformation("主视图模型已创建：Greeting={Greeting}", _greeting);
    }
}
