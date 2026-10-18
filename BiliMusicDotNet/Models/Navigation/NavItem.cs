using Avalonia.Media;
using BiliMusicDotNet.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliMusicDotNet.Models.Navigation;

/// <summary>
/// 导航项的位置分组：A 组钉在导航栏底部，B 组位于上方区块。
/// </summary>
/// <remarks>
/// 默认 <see cref="B"/>；桌面端侧边栏按它分上方 B 段与底部 A 段，并据此选择指示器动画，见 <c>docs/UI/导航控件文档.md</c>。
/// </remarks>
public enum NavGroup
{
    /// <summary>底部（钉底）。</summary>
    A,

    /// <summary>上方区块（默认）。</summary>
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
