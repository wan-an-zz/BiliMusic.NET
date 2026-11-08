using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Interactivity;
using CherylUI.Controls;

namespace BiliMusicDotNet.Controls;

/// <summary>
/// 设置页使用的 SliverPage：把内置返回按钮接到设置页的关闭命令。
/// </summary>
public sealed class SettingSliverPage : SliverPage
{
    /// <summary>沿用基类的模板与样式。</summary>
    protected override Type StyleKeyOverride => typeof(SliverPage);

    public static readonly StyledProperty<ICommand?> BackCommandProperty =
        AvaloniaProperty.Register<SettingSliverPage, ICommand?>(nameof(BackCommand));

    /// <summary>点击内置返回按钮时执行的命令。</summary>
    public ICommand? BackCommand
    {
        get => GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    /// <summary>设置页不在 MobileNavigation 栈里，返回只触发关闭命令。</summary>
    public override void GoBack(object? sender, RoutedEventArgs e)
    {
        if (BackCommand is { } command && command.CanExecute(null))
            command.Execute(null);
    }
}
