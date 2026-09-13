using Avalonia.Controls;
using BiliMusicDotNet.ViewModels;

namespace BiliMusicDotNet.Views;

public partial class MainView : UserControl
{
    /// <summary>
    /// 供 XAML 与设计器实例化（DataContext 由外层传入或继承）。
    /// </summary>
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 供容器实例化：视图模型由依赖注入提供。
    /// </summary>
    /// <param name="viewModel">该视图对应的视图模型。</param>
    public MainView(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
