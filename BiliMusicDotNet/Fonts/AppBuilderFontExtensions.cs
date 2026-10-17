using Avalonia;
using Avalonia.Media;

namespace BiliMusicDotNet.Fonts;

/// <summary>
/// 字体注册入口，供各平台在构建 AppBuilder 时调用。
/// </summary>
public static class AppBuilderFontExtensions
{
    /// <summary>
    /// 注册 HarmonyOS Sans 集合，并把默认字体族指向它：未显式指定字体的控件（即 <c>$Default</c>）都会用它。
    /// </summary>
    /// <param name="appBuilder">平台入口正在构建的 AppBuilder。</param>
    /// <remarks>
    /// 主题自带的字体资源键另在 App.axaml 覆盖，两者缺一都会让部分控件回落到系统字体。
    /// </remarks>
    public static AppBuilder WithHarmonyOsSansFont(this AppBuilder appBuilder)
    {
        return appBuilder
            .ConfigureFonts(manager => manager.AddFontCollection(new HarmonyOsSansFontCollection()))
            .With(new FontManagerOptions { DefaultFamilyName = HarmonyOsSansFontCollection.FamilyReference });
    }
}
