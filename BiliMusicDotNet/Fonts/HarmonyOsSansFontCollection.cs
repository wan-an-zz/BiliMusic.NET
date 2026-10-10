using System;
using Avalonia.Media.Fonts;

namespace BiliMusicDotNet.Fonts;

/// <summary>
/// 把 <c>Assets/Fonts</c> 下的 HarmonyOS Sans 字重注册为一个字体集合。
/// </summary>
/// <remarks>
/// 集合键是 XAML 里的字体来源前缀，写法为 <c>{集合键}#{族名}</c>，参见 <see cref="FamilyReference"/>。
/// </remarks>
public sealed class HarmonyOsSansFontCollection : EmbeddedFontCollection
{
    /// <summary>字体集合键，同时充当字体来源的 URI 前缀。</summary>
    public const string CollectionKey = "fonts:HarmonyOsSans";

    /// <summary>字体族名。Medium 字重的 legacy 族名带字重后缀，只能用 typographic 族名统一引用。</summary>
    public const string FamilyName = "HarmonyOS Sans SC";

    /// <summary>可直接写进 <c>FontFamily</c> 的引用串。</summary>
    public const string FamilyReference = CollectionKey + "#" + FamilyName;

    /// <summary>登记字体目录；构造时即扫描该目录，因此必须在 Avalonia 初始化之后调用。</summary>
    public HarmonyOsSansFontCollection() : base(
        new Uri(CollectionKey, UriKind.Absolute),
        new Uri("avares://BiliMusicDotNet/Assets/Fonts", UriKind.Absolute))
    {
    }
}
