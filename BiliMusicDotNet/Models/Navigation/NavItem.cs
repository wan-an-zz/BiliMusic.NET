using Avalonia.Media;
using BiliMusicDotNet.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliMusicDotNet.Models.Navigation;

/// <summary>
/// 导航项的位置分组：A 组位于导航栏两端，B 组位于中间。
/// </summary>
/// <remarks>
/// 桌面端侧边栏靠它决定选中指示器走哪种动画，见 <c>docs/UI/导航控件文档.md</c>。
/// </remarks>
public enum NavGroup
{
    /// <summary>两端位置（顶部与底部）。</summary>
    A,

    /// <summary>中间位置。</summary>
    B,
}

/// <summary>
/// 一个导航入口：图标、显示名称与目标视图模型。
/// </summary>
/// <remarks>
/// 桌面端与安卓端导航控件读同一份集合，因此两侧需要的字段都收在这里：
/// 桌面端只用 <see cref="Icon"/> 与 <see cref="Group"/>，安卓端只用 <see cref="Icon"/> 与 <see cref="Label"/>。
/// </remarks>
public sealed partial class NavItem : ObservableObject
{
    /// <summary>
    /// 导航项名称，移动端显示在图标下方。
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// 图标路径数据，取自 <c>Styles/Icons.axaml</c> 的资源。
    /// </summary>
    public Geometry? Icon { get; init; }

    /// <summary>
    /// 该项在导航栏中的位置分组。
    /// </summary>
    public NavGroup Group { get; init; } = NavGroup.B;

    /// <summary>
    /// 选中该项时展示的视图模型，由 <see cref="ViewLocator"/> 解析成视图。
    /// </summary>
    public ViewModelBase? Content { get; init; }

    /// <summary>
    /// 是否处于选中态。导航控件写、模板读，业务侧不要手工赋值。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;
}
