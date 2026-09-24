using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BiliMusicDotNet.Models.Navigation;

/// <summary>
/// 导航图标资源的取用门面：把 <c>Styles/Icons.axaml</c> 里的资源键与读取方式收在一处。
/// </summary>
/// <remarks>
/// 图标几何定义在 axaml 资源里而不是代码里，这里只负责按 key 取回并做类型校验，
/// 调用方不必再关心 <c>Application.Resources</c> 与 <c>StreamGeometry</c> 的细节。
/// </remarks>
public static class NavIcon
{
    /// <summary>首页图标资源键。</summary>
    public const string Home = "IconHome";

    /// <summary>搜索图标资源键。</summary>
    public const string Search = "IconSearch";

    /// <summary>我的图标资源键。</summary>
    public const string User = "IconUser";

    /// <summary>设置图标资源键。</summary>
    public const string Settings = "IconSettings";

    /// <summary>
    /// 按资源键取导航图标。
    /// </summary>
    /// <param name="key">资源键，取 <see cref="Home"/> 等常量。</param>
    /// <returns>图标几何；资源未注册、类型不符或取不到时返回 <c>null</c>，不抛异常。</returns>
    public static Geometry? Resolve(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        // 资源在 Application 上注册；设计器或单元测试等无应用宿主场景下静默返回空。
        if (Application.Current is not { } application)
            return null;

        return application.TryFindResource(key, out var value) && value is Geometry geometry
            ? geometry
            : null;
    }
}
