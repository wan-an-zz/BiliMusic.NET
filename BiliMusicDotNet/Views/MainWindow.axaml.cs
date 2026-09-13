using Avalonia.Controls;
using BiliMusicDotNet.ViewModels;

namespace BiliMusicDotNet.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// 供 XAML 与设计器实例化（DataContext 由外层传入）。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 供容器实例化：视图模型由依赖注入提供。
    /// </summary>
    /// <param name="viewModel">该窗口对应的视图模型。</param>
    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
